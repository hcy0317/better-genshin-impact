// Package nativeflow interprets a bounded, source-preserving BetterGI model.
// It never sends operating-system input and never supplies character damage data.
package nativeflow

import (
	"fmt"
	"math"
	"slices"
	"strconv"
	"strings"
	"unicode"
)

type Expression struct {
	Op       string      `json:"op"`
	Name     string      `json:"name,omitempty"`
	Argument string      `json:"argument,omitempty"`
	Left     *Expression `json:"left,omitempty"`
	Right    *Expression `json:"right,omitempty"`
}
type Node struct {
	ID         string            `json:"id"`
	Kind       string            `json:"kind"`
	Character  string            `json:"character"`
	Args       []string          `json:"args,omitempty"`
	Options    map[string]string `json:"options,omitempty"`
	Condition  *Expression       `json:"condition,omitempty"`
	Requires   *Expression       `json:"requires,omitempty"`
	Seconds    float64           `json:"seconds,omitempty"`
	Line       int               `json:"line"`
	Column     int               `json:"column,omitempty"`
	Start      int               `json:"start,omitempty"`
	End        int               `json:"end,omitempty"`
	ValueStart int               `json:"valueStart,omitempty"`
	ValueEnd   int               `json:"valueEnd,omitempty"`
}
type Block struct {
	Declaration Node   `json:"declaration"`
	Nodes       []Node `json:"nodes"`
	Macro       string `json:"macro,omitempty"`
	End         int    `json:"end,omitempty"`
}
type Program struct {
	SchemaVersion string                       `json:"schemaVersion"`
	Source        string                       `json:"source"`
	Loop          bool                         `json:"loop"`
	Root          []Node                       `json:"root"`
	Blocks        map[string]Block             `json:"blocks"`
	Timings       map[string]map[string]string `json:"timings"`
}

func (p *Program) Validate() error {
	if p == nil || p.SchemaVersion != "native-flow-v1" || len(p.Source) == 0 || len(p.Source) > 400000 || len(p.Root) == 0 || len(p.Blocks) > 32 || len(p.Timings) > 32 {
		return fmt.Errorf("native_flow: invalid program")
	}
	all := append([]Node(nil), p.Root...)
	macroNodes := map[string]bool{}
	for name, b := range p.Blocks {
		if !validName(name) {
			return b.Declaration.Error("片段名无效")
		}
		all = append(all, b.Declaration)
		all = append(all, b.Nodes...)
		if b.Macro != "" {
			if b.Macro != "neuvillette_charge_v1" || !validMacro(b) {
				return b.Declaration.Error("宏映射与完整输入块不匹配")
			}
			for _, n := range b.Nodes {
				macroNodes[n.ID] = true
			}
		}
	}
	if len(all) > 600 {
		return fmt.Errorf("native_flow: too many nodes")
	}
	seen, records := map[string]bool{}, map[string]bool{}
	for _, n := range all {
		if r := n.Options["record"]; r != "" {
			records[r] = true
		}
	}
	for _, n := range all {
		if n.ID == "" || len(n.ID) > 80 || seen[n.ID] {
			return n.Error("重复或无效节点标识")
		}
		seen[n.ID] = true
		if err := validateNode(n, macroNodes[n.ID], records); err != nil {
			return err
		}
		if name := n.Options["timing"]; name != "" {
			if _, ok := p.Timings[name]; !ok {
				return n.Error("未定义timing")
			}
		}
		for _, name := range targets(n) {
			if _, ok := p.Blocks[name]; !ok {
				return n.Error("未定义片段：" + name)
			}
		}
	}
	for name, t := range p.Timings {
		if !validName(name) || len(t) == 0 {
			return fmt.Errorf("native_flow: 无效timing")
		}
		for k, v := range t {
			if k != "cd" && k != "duration" {
				return fmt.Errorf("native_flow: 未支持timing字段")
			}
			if !validNumber(v, 0, 600) || (k == "duration" && option(t, k, 0) <= 0) {
				return fmt.Errorf("native_flow: 无效timing数值")
			}
		}
	}
	var visit func(string, map[string]bool, map[string]bool) error
	visit = func(name string, path, done map[string]bool) error {
		if path[name] {
			return fmt.Errorf("native_flow: 片段/恢复/维护存在循环调用：%s", name)
		}
		if done[name] {
			return nil
		}
		path[name] = true
		b := p.Blocks[name]
		nodes := append([]Node{b.Declaration}, b.Nodes...)
		for _, n := range nodes {
			for _, next := range targets(n) {
				if err := visit(next, path, done); err != nil {
					return err
				}
			}
		}
		delete(path, name)
		done[name] = true
		return nil
	}
	done := map[string]bool{}
	for name := range p.Blocks {
		if err := visit(name, map[string]bool{}, done); err != nil {
			return err
		}
	}
	return nil
}
func targets(n Node) []string {
	values := []string{}
	if n.Kind == "call" && len(n.Args) == 1 {
		values = append(values, n.Args[0])
	}
	for _, k := range []string{"then", "else", "unknown", "onfail", "watch-target"} {
		if name := n.Options[k]; name != "" {
			values = append(values, name)
		}
	}
	return values
}
func validateNode(n Node, macro bool, records map[string]bool) error {
	if n.Options["if"] != "" && n.Condition == nil || n.Options["requires"] != "" && n.Requires == nil {
		return n.Error("条件原文缺少对应的结构化条件，不能忽略")
	}
	if n.Requires != nil && n.Kind != "segment" {
		return n.Error("requires只能绑定片段")
	}
	allowed := ""
	switch n.Kind {
	case "segment":
		allowed = "required atomic timeout record requires onfail"
	case "branch":
		allowed = "if then else unknown required"
		if n.Condition == nil || n.Options["then"] == "" {
			return n.Error("分支缺少条件/then")
		}
	case "call":
		allowed = "required if once timeout attempts"
		if len(n.Args) != 1 || !validName(n.Args[0]) {
			return n.Error("call缺少有效目标")
		}
	case "skill":
		allowed = "hold fast wait required timeout if record maintain watch watch-mode watch-target before timing keep feed"
	case "burst":
		allowed = "required timeout attempts no-progress if keep record timing"
	case "attack", "wait":
		allowed = "required timeout if keep"
		if !finite(n.Seconds) || n.Seconds <= 0 || n.Seconds > 30 {
			return n.Error("无效动作秒数")
		}
	case "check":
	case "keydown":
		allowed = "required keep"
		if !macro {
			return n.Error("输入命令不在受支持宏中")
		}
	case "keyup", "moveby":
		if !macro {
			return n.Error("输入命令不在受支持宏中")
		}
	default:
		return n.Error("未支持动作：" + n.Kind)
	}
	if n.Kind != "segment" && n.Kind != "call" && n.Kind != "branch" && !validKey(n.Character) {
		return n.Error("动作角色键无效")
	}
	for k, v := range n.Options {
		if !slices.Contains(strings.Fields(allowed), k) || len(v) > 2048 {
			return n.Error("未支持参数：" + k)
		}
		switch k {
		case "required", "atomic", "hold", "fast", "wait":
			if v != "true" {
				return n.Error("标志值无效：" + k)
			}
		case "timeout":
			if !validNumber(v, 0.01, 600) {
				return n.Error("timeout无效")
			}
		case "before":
			if !validNumber(v, 0, 600) {
				return n.Error("before无效")
			}
		case "attempts", "no-progress":
			if !validNumber(v, 1, 64) || option(n.Options, k, 0) != math.Trunc(option(n.Options, k, 0)) {
				return n.Error("次数无效")
			}
		case "once":
			if v != "battle" {
				return n.Error("once仅支持battle")
			}
		case "record", "keep", "maintain", "watch", "timing", "onfail", "watch-target", "then", "else", "unknown":
			if !validName(v) {
				return n.Error("名称无效：" + k)
			}
		case "feed":
			if !validKey(v) {
				return n.Error("接球队员无效")
			}
		}
	}
	if n.Options["watch"] != "" && (n.Options["watch-mode"] != "call" || n.Options["watch-target"] == "" || n.Options["watch"] != n.Options["record"] || n.Options["watch"] != n.Options["maintain"]) {
		return n.Error("watch须绑定同一record/maintain和调用目标")
	}
	if n.Options["watch"] == "" && (n.Options["watch-mode"] != "" || n.Options["watch-target"] != "") {
		return n.Error("维护调用缺少watch声明")
	}
	for _, k := range []string{"keep", "maintain", "watch"} {
		if r := n.Options[k]; r != "" && !records[r] {
			return n.Error("未定义记录：" + r)
		}
	}
	for _, expr := range []*Expression{n.Condition, n.Requires} {
		if expr != nil {
			if err := validateExpression(expr, 0, records); err != nil {
				return n.Error(err.Error())
			}
		}
	}
	return nil
}
func validateExpression(e *Expression, depth int, records map[string]bool) error {
	if e == nil || depth > 32 {
		return fmt.Errorf("无效或过深条件")
	}
	switch e.Op {
	case "call":
		switch e.Name {
		case "record-active", "record-exists":
			if !records[e.Argument] {
				return fmt.Errorf("未定义记录：%s", e.Argument)
			}
			return nil
		case "low-hp", "q-ready", "q-cd", "q-energy-low", "e-ready", "e-cd":
			if !validKey(e.Argument) {
				return fmt.Errorf("条件角色无效")
			}
			return nil
		}
	case "not":
		return validateExpression(e.Left, depth+1, records)
	case "and", "or":
		if err := validateExpression(e.Left, depth+1, records); err != nil {
			return err
		}
		return validateExpression(e.Right, depth+1, records)
	}
	return fmt.Errorf("未支持条件")
}
func validMacro(b Block) bool {
	if b.Declaration.Options["atomic"] != "true" || len(b.Nodes) < 3 {
		return false
	}
	for i, n := range b.Nodes {
		if n.Character != "neuvillette" || n.Condition != nil {
			return false
		}
		if i == 0 || i == len(b.Nodes)-1 {
			expected := "keydown"
			if i > 0 {
				expected = "keyup"
			}
			if n.Kind != expected || len(n.Args) != 1 || n.Args[0] != "VK_LBUTTON" {
				return false
			}
		} else if n.Kind == "wait" {
			if len(n.Options) > 0 {
				return false
			}
		} else if n.Kind == "moveby" {
			if len(n.Args) != 2 || len(n.Options) > 0 {
				return false
			}
			for _, v := range n.Args {
				if !validNumber(v, -10000, 10000) {
					return false
				}
			}
		} else {
			return false
		}
	}
	return true
}
func validName(s string) bool {
	if len(s) == 0 || len(s) > 240 {
		return false
	}
	for _, r := range s {
		if !unicode.IsLetter(r) && !unicode.IsNumber(r) && r != '_' && r != '-' && r != '·' {
			return false
		}
	}
	return true
}
func validKey(s string) bool {
	if len(s) == 0 || len(s) > 50 {
		return false
	}
	for _, r := range s {
		if !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9') {
			return false
		}
	}
	return true
}
func finite(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }
func validNumber(s string, min, max float64) bool {
	v, err := strconv.ParseFloat(s, 64)
	return err == nil && finite(v) && v >= min && v <= max
}
func option(options map[string]string, key string, fallback float64) float64 {
	if v, ok := options[key]; ok {
		n, _ := strconv.ParseFloat(v, 64)
		return n
	}
	return fallback
}
func (n Node) Error(message string) error {
	return fmt.Errorf("native_flow: 第%d行（%s）：%s", n.Line, n.ID, message)
}
