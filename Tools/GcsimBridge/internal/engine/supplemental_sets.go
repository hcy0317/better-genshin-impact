package engine

import (
	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/attacks"
	"github.com/genshinsim/gcsim/pkg/core/attributes"
	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/core/keys"
	"github.com/genshinsim/gcsim/pkg/core/player/character"
	"github.com/genshinsim/gcsim/pkg/modifier"
	"strings"
)

const SupplementalSetRevision = "8b15995fa220c88a4d0d7ffe1e21b041d0b32588"
const heartOfTheFurnace = "heartofthefurnace"
const scarletProof = "scarletproof"

func isSupplementalSet(key string) bool {
	key = strings.ToLower(key)
	if key != heartOfTheFurnace && key != scarletProof {
		return false
	}
	_, err := keys.SetString(key)
	return err != nil
}

// AttachEquipmentExtensions binds the two versioned supplemental artifact sets
// to the same SDK core used for the evaluated party. Unknown sets are not admitted.
func AttachEquipmentExtensions(c *core.Core, equipment map[string][]Artifact) {
	four := map[int]map[string]bool{}
	for _, actor := range c.Player.Chars() {
		counts := map[string]int{}
		for _, piece := range equipment[actor.Base.Key.String()] {
			counts[strings.ToLower(piece.SetKey)]++
		}
		for _, key := range []string{heartOfTheFurnace, scarletProof} {
			if !isSupplementalSet(key) || counts[key] < 4 {
				continue
			}
			if counts[key] >= 4 {
				if four[actor.Index()] == nil {
					four[actor.Index()] = map[string]bool{}
				}
				four[actor.Index()][key] = true
			}
		}
	}
	if len(four) == 0 {
		return
	}
	heart := func(index int) {
		if !four[index][heartOfTheFurnace] {
			return
		}
		owner := c.Player.ByIndex(index)
		m := make([]float64, attributes.EndStatType)
		m[attributes.ATKP] = .12
		owner.AddStatMod(character.StatMod{Base: modifier.NewBase("bettergi/heart/atk", 720), AffectedStat: attributes.ATKP, Amount: func() []float64 { return m }})
		for _, member := range c.Player.Chars() {
			member.AddReactBonusMod(character.ReactBonusMod{Base: modifier.NewBase("bettergi/heart/team", 720), Amount: func(ai info.AttackInfo) float64 {
				if ai.AttackTag.IsStellar() {
					return .5
				}
				return 0
			}})
		}
	}
	stellarReaction := func(swirl bool) func(...any) {
		return func(args ...any) {
			if len(args) < 2 {
				return
			}
			ae, ok := args[1].(*info.AttackEvent)
			if !ok {
				return
			}
			index := ae.Info.ActorIndex
			heart(index)
			if swirl && four[index][scarletProof] {
				owner := c.Player.ByIndex(index)
				m := make([]float64, attributes.EndStatType)
				m[attributes.CR] = .16
				owner.AddStatMod(character.StatMod{Base: modifier.NewBase("bettergi/scarlet/cr", 600), AffectedStat: attributes.CR, Amount: func() []float64 { return m }})
				owner.AddReactBonusMod(character.ReactBonusMod{Base: modifier.NewBase("bettergi/scarlet/swirl", 600), Amount: func(ai info.AttackInfo) float64 {
					if ai.AttackTag == attacks.AttackTagReactionStellarSwirl || ai.AttackTag == attacks.AttackTagDirectStellarSwirl {
						return .4
					}
					return 0
				}})
			}
		}
	}
	c.Events.Subscribe(event.OnStellarConduct, stellarReaction(false), "bettergi/supplemental/stellar-conduct")
	c.Events.Subscribe(event.OnStellarSwirl, stellarReaction(true), "bettergi/supplemental/stellar-swirl")
	c.Events.Subscribe(event.OnEnemyDamage, func(args ...any) {
		if len(args) < 3 {
			return
		}
		ae, ok := args[1].(*info.AttackEvent)
		damage, positive := args[2].(float64)
		if ok && positive && damage > 0 && ae.Info.AttackTag.IsStellar() {
			heart(ae.Info.ActorIndex)
		}
	}, "bettergi/supplemental/stellar-damage")
}
