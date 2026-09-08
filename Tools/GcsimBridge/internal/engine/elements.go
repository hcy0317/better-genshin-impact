package engine

import "github.com/genshinsim/gcsim/pkg/core/attributes"

// This is the manual-Buff input domain, not a second reaction database. Named
// constants are stable across upstream Element API/ordinal changes.
func damageElement(name string) (attributes.Element, bool) {
	switch name {
	case "pyro":
		return attributes.Pyro, true
	case "hydro":
		return attributes.Hydro, true
	case "cryo":
		return attributes.Cryo, true
	case "electro":
		return attributes.Electro, true
	case "anemo":
		return attributes.Anemo, true
	case "geo":
		return attributes.Geo, true
	case "dendro":
		return attributes.Dendro, true
	case "physical":
		return attributes.Physical, true
	default:
		return attributes.NoElement, false
	}
}
