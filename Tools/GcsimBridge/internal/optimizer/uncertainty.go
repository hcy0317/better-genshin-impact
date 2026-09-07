package optimizer

import "math"

type Improvement struct {
	State     string  `json:"state"`
	Samples   int     `json:"samples"`
	MeanDelta float64 `json:"meanDelta"`
	Lower     float64 `json:"lower"`
	Upper     float64 `json:"upper"`
	Method    string  `json:"method"`
}

// A paired 95% Student interval on the independent batch, conditional on the
// declared scenarios/seeds. Never a universal random guarantee or global optimum.
func compareImprovement(mode string, weights map[string]float64, candidate, baseline *Plan, n int) *Improvement {
	result := &Improvement{State: "uncertain", Samples: n, Method: "paired_student_95_actual_weighted_dps; finite_batch_only"}
	if mode != "balanced" {
		result.Method = "independent_role_ranking; confidence_not_established"
		return result
	}
	if n < 2 {
		return result
	}
	deltas := make([]float64, n)
	for id, weight := range weights {
		a, b := candidate.Reports[id].ScoredDPS, baseline.Reports[id].ScoredDPS
		if len(a) != n || len(b) != n {
			return result
		}
		for i := range deltas {
			deltas[i] += weight * (a[i] - b[i])
		}
	}
	for _, d := range deltas {
		if !finite(d) {
			return result
		}
		result.MeanDelta += d / float64(n)
	}
	variance := 0.0
	for _, d := range deltas {
		variance += (d - result.MeanDelta) * (d - result.MeanDelta) / float64(n-1)
	}
	critical := 2.042 // conservative for df >= 30
	tValues := []float64{0, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045}
	if n-1 < len(tValues) {
		critical = tValues[n-1]
	}
	radius := critical * math.Sqrt(variance/float64(n))
	result.Lower, result.Upper = result.MeanDelta-radius, result.MeanDelta+radius
	if finite(result.Lower) && result.Lower > 0 {
		result.State = "confirmed"
	}
	return result
}
