package engine

import (
	"errors"
	"fmt"
	"math"

	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/action"
	"github.com/genshinsim/gcsim/pkg/core/attacks"
	"github.com/genshinsim/gcsim/pkg/core/attributes"
	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/core/player/character"
	"github.com/genshinsim/gcsim/pkg/modifier"
)

type Buff struct {
	ID              string   `json:"id"`
	Kind            string   `json:"kind"`
	Target          string   `json:"target"`
	Stat            string   `json:"stat,omitempty"`
	Element         string   `json:"element,omitempty"`
	AttackTag       string   `json:"attackTag,omitempty"`
	Value           float64  `json:"value"`
	Unit            string   `json:"unit"`
	DurationFrames  int      `json:"durationFrames"`
	Anchor          string   `json:"anchor"`
	SourceCharacter string   `json:"sourceCharacter,omitempty"`
	Action          string   `json:"action,omitempty"`
	Relationship    string   `json:"relationship"`
	Source          string   `json:"source,omitempty"`
	CoverageRatio   *float64 `json:"coverageRatio,omitempty"`
}

type BuffActivation struct {
	ID        string `json:"id"`
	Frame     int    `json:"frame"`
	ExpiresAt int    `json:"expiresAt"`
}

func prepareBuffs(cfg *info.ActionList, buffs []Buff, rounds []Round) ([]Buff, error) {
	if len(buffs) > 32 {
		return nil, errors.New("at most 32 manual buffs are supported")
	}
	seen := make(map[string]bool)
	resolved := make([]Buff, 0, len(buffs))
	for _, buff := range buffs {
		if buff.ID == "" || len(buff.ID) > 80 || seen[buff.ID] || math.IsNaN(buff.Value) || math.IsInf(buff.Value, 0) {
			return nil, errors.New("invalid manual buff identity/value")
		}
		seen[buff.ID] = true
		if buff.CoverageRatio != nil {
			ratio := *buff.CoverageRatio
			if math.IsNaN(ratio) || math.IsInf(ratio, 0) || ratio < 0 || ratio > 1 || buff.Kind != "stat" || buff.Anchor != "start" || buff.DurationFrames != -1 {
				return nil, errors.New("coverage-ratio approximation requires a permanent starting stat buff")
			}
			buff.Value *= ratio
		}
		if buff.Relationship == "" {
			buff.Relationship = "pending_review"
		}
		if buff.Relationship != "additional" && buff.Relationship != "pending_review" {
			return nil, errors.New("native effect replacement is not supported")
		}
		if buff.DurationFrames == 0 || buff.DurationFrames < -1 || buff.DurationFrames > 36000 {
			return nil, errors.New("unsupported manual buff combination")
		}
		if buff.Anchor == "action" {
			if buff.Action != "attack" && buff.Action != "skill" && buff.Action != "burst" {
				return nil, errors.New("unsupported action anchor")
			}
			found := false
			for _, profile := range cfg.Characters {
				if profile.Base.Key.String() == buff.SourceCharacter {
					found = true
				}
			}
			if !found {
				return nil, errors.New("buff source character is absent from this scenario")
			}
		} else if (buff.Anchor != "start" && buff.Anchor != "round") || buff.SourceCharacter != "" || buff.Action != "" {
			return nil, errors.New("unsupported buff anchor")
		}
		if buff.Anchor == "round" && validateRounds(rounds, int(cfg.Settings.Duration*60)) != nil {
			return nil, errors.New("round buffs require explicit valid simulation windows")
		}
		if err := prepareBuffEffect(cfg, buff); err != nil {
			return nil, err
		}
		resolved = append(resolved, buff)
	}
	return resolved, nil
}

func prepareBuffEffect(cfg *info.ActionList, buff Buff) error {
	if buff.Kind == "resistance" || buff.Kind == "defense_reduction" {
		if buff.Target != "all_enemies" || buff.Unit != "fraction" || buff.Stat != "" || buff.AttackTag != "" {
			return errors.New("invalid enemy modifier scope/unit")
		}
		if buff.Kind == "resistance" {
			if attributes.EleToDmgP(attributes.StringToEle(buff.Element)) < 0 {
				return errors.New("invalid resistance element")
			}
		} else if buff.Element != "" || buff.Value < 0 || buff.Value > 1 {
			return errors.New("defense reduction must be a fraction from 0 to 1")
		}
		return nil
	}
	stat := attributes.StrToStatType(buff.Stat)
	switch buff.Kind {
	case "stat":
		if stat <= attributes.NoStat || stat >= attributes.DelimBaseStat || buff.AttackTag != "" || buff.Element != "" {
			return fmt.Errorf("unsupported manual stat %q", buff.Stat)
		}
		flat := stat == attributes.HP || stat == attributes.ATK || stat == attributes.DEF || stat == attributes.EM
		if (flat && buff.Unit != "flat") || (!flat && buff.Unit != "fraction") {
			return errors.New("manual buff unit must match its native stat")
		}
	case "attack_bonus":
		if buff.Unit != "fraction" || buff.Element != "" || buff.Stat != "" || (buff.AttackTag != "normal" && buff.AttackTag != "skill" && buff.AttackTag != "burst") {
			return errors.New("unsupported attack-tag modifier")
		}
	default:
		return errors.New("unsupported manual buff kind")
	}
	for i := range cfg.Characters {
		profile := &cfg.Characters[i]
		if profile.Base.Key.String() == buff.Target {
			if buff.Kind == "stat" && buff.Anchor == "start" && buff.DurationFrames == -1 {
				profile.Stats[stat] += buff.Value
			}
			return nil
		}
	}
	return errors.New("manual buff target is absent from the scenario")
}

func applyBuffEffect(c *core.Core, buff Buff, horizon int) {
	duration := buff.DurationFrames
	// This pinned engine's enemy def/res readers check expiry > frame, unlike stat
	// modifiers which also recognize -1. Bind a permanent enemy effect to this run.
	if duration == -1 && (buff.Kind == "resistance" || buff.Kind == "defense_reduction") {
		duration = max(1, horizon-c.F+1)
	}
	base := modifier.NewBase("bettergi/manual/"+buff.ID, duration)
	if buff.Kind == "resistance" || buff.Kind == "defense_reduction" {
		for _, target := range c.Combat.Enemies() {
			enemy := target.(info.Enemy)
			if buff.Kind == "resistance" {
				enemy.AddResistMod(info.ResistMod{Base: base, Ele: attributes.StringToEle(buff.Element), Value: buff.Value})
			} else {
				enemy.AddDefMod(info.DefMod{Base: base, Dur: duration, Value: -buff.Value})
			}
		}
		return
	}
	stat := attributes.StrToStatType(buff.Stat)
	if buff.Kind == "attack_bonus" {
		stat = attributes.DmgP
	}
	values := make([]float64, attributes.EndStatType)
	values[stat] = buff.Value
	for _, target := range c.Player.Chars() {
		if target.Base.Key.String() != buff.Target {
			continue
		}
		if buff.Kind == "stat" {
			target.AddStatMod(character.StatMod{Base: base, AffectedStat: stat, Amount: func() []float64 { return values }})
		} else {
			target.AddAttackMod(character.AttackMod{Base: base, Amount: func(attack *info.AttackEvent, _ info.Target) []float64 {
				tag := attack.Info.AttackTag
				matches := (buff.AttackTag == "normal" && tag == attacks.AttackTagNormal) || (buff.AttackTag == "skill" && (tag == attacks.AttackTagElementalArt || tag == attacks.AttackTagElementalArtHold)) || (buff.AttackTag == "burst" && tag == attacks.AttackTagElementalBurst)
				if matches {
					return values
				}
				return nil
			}})
		}
	}
}

func attachBuffs(c *core.Core, buffs []Buff, rounds []Round, horizon int) *[]BuffActivation {
	events := make([]BuffActivation, 0, len(buffs))
	for _, buff := range buffs {
		if buff.Kind == "stat" && buff.Anchor == "start" && buff.DurationFrames == -1 {
			events = append(events, BuffActivation{ID: buff.ID, Frame: 0, ExpiresAt: -1})
			continue
		}
		activate := func() {
			applyBuffEffect(c, buff, horizon)
			expires := -1
			if buff.DurationFrames >= 0 {
				expires = c.F + buff.DurationFrames
			}
			events = append(events, BuffActivation{ID: buff.ID, Frame: c.F, ExpiresAt: expires})
		}
		if buff.Anchor == "start" {
			activate()
		} else if buff.Anchor == "round" {
			for _, round := range rounds {
				if round.StartFrame == 0 {
					activate()
				}
			}
			c.Events.Subscribe(event.OnTick, func(args ...any) {
				for _, round := range rounds {
					if c.F == round.StartFrame && c.F != 0 {
						activate()
					}
				}
			}, "bettergi/round-buff/"+buff.ID)
		} else {
			c.Events.Subscribe(event.OnActionExec, func(args ...any) {
				actor := c.Player.Chars()[args[0].(int)]
				if actor.Base.Key.String() == buff.SourceCharacter && args[1].(action.Action).String() == buff.Action {
					activate()
				}
			}, "bettergi/buff-anchor/"+buff.ID)
		}
	}
	return &events
}
