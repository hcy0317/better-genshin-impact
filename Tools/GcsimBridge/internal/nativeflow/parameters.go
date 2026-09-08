package nativeflow

import (
	"math"
	"sort"
	"strconv"
	"unicode/utf16"
)

type Parameter struct {
	Node     string  `json:"node"`
	Line     int     `json:"line"`
	Kind     string  `json:"kind"`
	Original float64 `json:"original"`
	Value    float64 `json:"value"`
	Min      float64 `json:"min"`
	Max      float64 `json:"max"`
}

func (p *Program) Parameters() []Parameter {
	locked := map[string]bool{}
	var lock func(string)
	lock = func(name string) {
		if locked[name] {
			return
		}
		locked[name] = true
		b := p.Blocks[name]
		for _, n := range append([]Node{b.Declaration}, b.Nodes...) {
			for _, target := range targets(n) {
				lock(target)
			}
		}
	}
	for name, b := range p.Blocks {
		if b.Macro != "" || b.Declaration.Options["atomic"] == "true" {
			lock(name)
		}
	}
	units := utf16.Encode([]rune(p.Source))
	result := []Parameter{}
	add := func(nodes []Node) {
		for _, n := range nodes {
			if n.Kind != "wait" && n.Kind != "attack" || n.ValueStart < 0 || n.ValueEnd <= n.ValueStart || n.ValueEnd > len(units) {
				continue
			}
			original, err := strconv.ParseFloat(string(utf16.Decode(units[n.ValueStart:n.ValueEnd])), 64)
			if err != nil || original != n.Seconds {
				continue
			}
			result = append(result, Parameter{n.ID, n.Line, n.Kind, n.Seconds, n.Seconds, math.Max(.05, n.Seconds-.5), math.Min(30, n.Seconds+.5)})
		}
	}
	add(p.Root)
	for name, b := range p.Blocks {
		if !locked[name] {
			add(b.Nodes)
		}
	}
	sort.Slice(result, func(i, j int) bool {
		if result[i].Line == result[j].Line {
			return result[i].Node < result[j].Node
		}
		return result[i].Line < result[j].Line
	})
	return result
}
func (p *Program) WithParameters(values []Parameter) *Program {
	changed := map[string]float64{}
	for _, v := range values {
		changed[v.Node] = v.Value
	}
	copyNodes := func(nodes []Node) []Node {
		out := append([]Node(nil), nodes...)
		for i := range out {
			if value, ok := changed[out[i].ID]; ok {
				out[i].Seconds = value
				out[i].Args = append([]string(nil), out[i].Args...)
				if len(out[i].Args) > 0 {
					out[i].Args[0] = strconv.FormatFloat(value, 'f', -1, 64)
				}
			}
		}
		return out
	}
	next := *p
	next.Root = copyNodes(p.Root)
	next.Blocks = map[string]Block{}
	for name, b := range p.Blocks {
		b.Nodes = copyNodes(b.Nodes)
		next.Blocks[name] = b
	}
	return &next
}
