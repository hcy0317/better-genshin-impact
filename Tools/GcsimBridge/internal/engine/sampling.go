package engine

import (
	"github.com/genshinsim/gcsim/pkg/stats"
	"strconv"
)

const MaxEvaluationSamples = 1000
const MaxTrajectorySeconds = 600

type SampleMetricEvidence struct {
	Seed    string   `json:"seed"`
	Metrics []Metric `json:"metrics,omitempty"`
	Issue   string   `json:"issue,omitempty"`
}

// Frame-by-frame plotting buffers are not inputs to our damage, action, healing
// or energy checks. Release them before retaining a large validation batch.
func trimFrameVectors(result *stats.Result) {
	result.DamageBuckets = nil
	result.DamageMitigation = nil
	for i := range result.Characters {
		result.Characters[i].EnergyStatus = nil
		result.Characters[i].HealthStatus = nil
		result.Characters[i].DamageCumulativeContrib = nil
	}
	for i := range result.Enemies {
		result.Enemies[i].CumulativeDamage = nil
	}
}
func compactLargeSamples(report *Report, request Request, windows [][]Round) {
	if len(request.Seeds) <= 64 && !request.CompactSamples {
		return
	}
	report.SamplesCompacted = true
	for i, sample := range report.Samples {
		rounds := request.Rounds
		item := SampleMetricEvidence{Seed: strconv.FormatUint(sample.Seed, 10)}
		if request.AutoRounds {
			if i >= len(windows) || len(windows[i]) == 0 {
				item.Issue = "本样本没有完整可用轮次"
			} else {
				rounds = windows[i]
			}
		}
		if item.Issue == "" {
			var err error
			item.Metrics, err = SummarizeMetrics([]stats.Result{sample}, rounds)
			if err != nil {
				item.Issue = err.Error()
			}
		}
		report.SampleMetrics = append(report.SampleMetrics, item)
		minimal := stats.Result{Seed: sample.Seed, Duration: sample.Duration, TotalDamage: sample.TotalDamage, DPS: sample.DPS, TargetOverlap: sample.TargetOverlap, EndStats: sample.EndStats}
		for _, c := range sample.Characters {
			minimal.Characters = append(minimal.Characters, stats.CharacterResult{Name: c.Name, ActiveTime: c.ActiveTime, EnergySpent: c.EnergySpent})
		}
		report.Samples[i] = minimal
	}
}
