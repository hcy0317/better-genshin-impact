// Package sandronemodel contains the source-labelled state model used while
// developing Sandrone's SDK extension. It does not register a runnable character.
package sandronemodel

// Power uses 54 subunits per displayed point to represent the reported 18s
// on-field cooling time exactly at 60fps, avoiding threshold drift from floats.
// Timing values are community estimates, not certified frame measurements.
type Power struct {
	units              int
	c1                 bool
	decoding           bool
	overheated         bool
	chargeFrames       int
	skillCoolingFrames int
}

func NewPower(c1 bool) *Power   { return &Power{c1: c1} }
func (p *Power) Value() float64 { return float64(p.units) / 54 }

// Units exposes the exact cooling amount for the SDK's ten-point stack counter.
func (p *Power) Units() int       { return p.units }
func (p *Power) Decoding() bool   { return p.decoding }
func (p *Power) Overheated() bool { return p.overheated }
func (p *Power) StartCharge() bool {
	if p.overheated || p.skillCoolingFrames > 0 {
		return false
	}
	p.decoding = true
	p.chargeFrames = 0
	return true
}

func (p *Power) StopCharge() { p.decoding = false; p.chargeFrames = 0 }

// StartSkillCooling models the reported ~0.5s full-gauge E cooling interval.
// The SDK action layer must separately implement the real skill animation/CD.
func (p *Power) StartSkillCooling() { p.StopCharge(); p.skillCoolingFrames = 30 }

// Advance advances one simulation frame. The caller supplies actual field state.
func (p *Power) Advance(onField bool) {
	if !onField {
		p.decoding = false
	}
	if !p.decoding {
		rate := 5
		if !onField {
			rate = 15
		}
		if p.skillCoolingFrames > 0 {
			rate = 180
			p.skillCoolingFrames--
		}
		p.units = max(0, p.units-rate)
		if p.units < 50*54 {
			p.overheated = false
		}
		return
	}
	p.chargeFrames++
	if p.chargeFrames%12 == 0 {
		p.gain(4 * 54)
	}
}

// FireBeam is called on firing, not on hit: the formal talent description ties
// power gain to firing, whereas particles will be wired to confirmed SDK hits.
func (p *Power) FireBeam() bool {
	if !p.decoding {
		return false
	}
	p.gain(12 * 54)
	return true
}

func (p *Power) gain(units int) {
	if p.c1 {
		units /= 2
	}
	p.units = min(100*54, p.units+units)
	if p.units == 100*54 {
		p.overheated = true
		p.decoding = false
	}
}
