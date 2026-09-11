package sandronemodel_test

import (
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sandronemodel"
)

func TestPowerReproducesReportedThreeAndSixBeamCycles(t *testing.T) {
	for _, tc := range []struct {
		name string
		c1   bool
		want int
	}{{"C0", false, 3}, {"C1", true, 6}} {
		t.Run(tc.name, func(t *testing.T) {
			p := sandronemodel.NewPower(tc.c1)
			if !p.StartCharge() {
				t.Fatal("empty power should permit decoding")
			}
			beams := 0
			for frame := 1; frame <= 600 && !p.Overheated(); frame++ {
				p.Advance(true)
				if frame >= 96 && (frame-96)%60 == 0 && p.FireBeam() {
					beams++
				}
			}
			if beams != tc.want || p.Value() != 100 || !p.Overheated() {
				t.Fatalf("beams=%d power=%v overheat=%v", beams, p.Value(), p.Overheated())
			}
			if p.StartCharge() || p.FireBeam() {
				t.Fatal("overheat must prevent decoding or additional condensed beams")
			}
		})
	}
}

func fullPower(t *testing.T) *sandronemodel.Power {
	t.Helper()
	p := sandronemodel.NewPower(false)
	p.StartCharge()
	for i := 0; i < 9; i++ {
		p.FireBeam()
	}
	if p.Value() != 100 {
		t.Fatal("fixture did not fill")
	}
	return p
}

func TestCoolingThresholdIsStrictAndOffFieldIsThreeTimesFaster(t *testing.T) {
	for _, tc := range []struct {
		name       string
		onField    bool
		half, full int
	}{{"on-field", true, 540, 1080}, {"off-field", false, 180, 360}} {
		t.Run(tc.name, func(t *testing.T) {
			p := fullPower(t)
			for i := 0; i < tc.half; i++ {
				p.Advance(tc.onField)
			}
			if p.Value() != 50 || !p.Overheated() || p.StartCharge() {
				t.Fatalf("must stay overheated at exactly50, got %v", p.Value())
			}
			p.Advance(tc.onField)
			if p.Overheated() {
				t.Fatal("must exit below50")
			}
			for i := tc.half + 1; i < tc.full; i++ {
				p.Advance(tc.onField)
			}
			if p.Value() != 0 {
				t.Fatalf("full cooling: %v", p.Value())
			}
			p.Advance(tc.onField)
			if p.Value() != 0 {
				t.Fatal("power underflow")
			}
		})
	}
}

func TestSkillCoolingAndChargeLifecycle(t *testing.T) {
	p := fullPower(t)
	p.StartSkillCooling()
	for i := 0; i < 15; i++ {
		p.Advance(true)
	}
	if p.Value() != 50 || !p.Overheated() {
		t.Fatalf("skill half-time power %v", p.Value())
	}
	p.Advance(true)
	if p.Overheated() {
		t.Fatal("skill cooling must exit overheat below50")
	}
	if p.StartCharge() {
		t.Fatal("cannot decode during the model's30f rapid cooling interval")
	}
	for i := 16; i < 30; i++ {
		p.Advance(true)
	}
	if p.Value() != 0 || !p.StartCharge() {
		t.Fatal("E should permit a new charge after cooling")
	}
	for i := 0; i < 11; i++ {
		p.Advance(true)
	}
	if p.Value() != 0 {
		t.Fatal("charge timer should restart")
	}
	p.Advance(true)
	if p.Value() != 4 {
		t.Fatalf("new charge first heat tick=%v", p.Value())
	}
	p.StopCharge()
	if p.FireBeam() {
		t.Fatal("ended charge emitted a beam")
	}
	p.Advance(true)
	if p.Value() >= 4 {
		t.Fatal("ending charge must resume cooling")
	}
	p.StartCharge()
	p.Advance(false)
	if p.FireBeam() {
		t.Fatal("switching off-field must stop decoding")
	}
}
