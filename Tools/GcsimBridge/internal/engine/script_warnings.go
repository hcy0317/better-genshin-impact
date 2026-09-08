package engine

import (
	"encoding/json"
	"fmt"
	"strings"
	"sync"

	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/gcs/ast"
)

var warningNamesOnce sync.Once
var warningNames struct {
	Characters map[string]string `json:"character_names"`
	Weapons    map[string]string `json:"weapon_names"`
}

// Advisory only: reachability and independent stopping conditions still
// belong to gcsim, so a warning must not disqualify a candidate.
func scriptWarnings(script ast.Node, file *ast.File, cfg *info.ActionList, offset int) []string {
	warningNamesOnce.Do(func() { _ = json.Unmarshal(chineseNames, &warningNames) })
	weapons := map[string]string{}
	for _, c := range cfg.Characters {
		weapon := c.Weapon.Name
		if weapon == "" {
			weapon = c.Weapon.Key.String()
		}
		weapons[c.Base.Key.String()] = weapon
	}
	var warnings []string
	var walk func(ast.Node)
	walk = func(node ast.Node) {
		if node == nil || len(warnings) >= 16 {
			return
		}
		switch n := node.(type) {
		case *ast.BlockStmt:
			if n != nil {
				for _, child := range n.List {
					walk(child)
				}
			}
		case *ast.ForStmt:
			walk(n.Body)
		case *ast.IfStmt:
			walk(n.IfBlock)
			walk(n.ElseBlock)
		case *ast.FnStmt:
			walk(n.Func.Body)
		case *ast.FuncLit:
			walk(n.Body)
		case *ast.LetStmt:
			if f, ok := n.Val.(*ast.FuncLit); ok {
				walk(f)
			}
		case *ast.SwitchStmt:
			for _, item := range n.Cases {
				walk(item.Body)
			}
			walk(n.Default)
		case *ast.WhileStmt:
			if unary, ok := n.Condition.(*ast.UnaryExpr); ok && unary.Op.Typ == ast.LogicNot {
				if field, ok := unary.Right.(*ast.Field); ok {
					path := field.Value
					if len(path) == 3 && path[1] == "mods" && path[2] == "favonius-cd" {
						if weapon, known := weapons[path[0]]; known && !strings.HasPrefix(weapon, "favonius") {
							characterName, weaponName := warningNames.Characters[path[0]], warningNames.Weapons[weapon]
							if characterName == "" {
								characterName = path[0]
							}
							if weaponName == "" {
								weaponName = weapon
							}
							warnings = append(warnings, fmt.Sprintf("循环第%d行：%s的等待条件要求西风武器触发，但当前装备%s；该分支若执行，等待条件不会因该武器被动满足。请检查循环依赖，敌人死亡和正常时长终止仍按原逻辑处理。", max(1, file.Position(n.Position()).Line-offset), characterName, weaponName))
						}
					}
				}
			}
			walk(n.WhileBlock)
		}
	}
	walk(script)
	return warnings
}
