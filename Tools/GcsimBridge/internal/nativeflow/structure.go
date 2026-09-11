package nativeflow

import "sort"

type StructuralEdit struct {
	Kind  string `json:"kind"`
	Block string `json:"block"`
	Node  string `json:"node"`
	Other string `json:"other,omitempty"`
}
type StructuralVariant struct {
	Program *Program
	Edit    StructuralEdit
}

func (p *Program) StructuralComplexity() int {
	count := 0
	var expression func(*Expression)
	expression = func(e *Expression) {
		if e == nil {
			return
		}
		count++
		expression(e.Left)
		expression(e.Right)
	}
	add := func(nodes []Node) {
		for _, n := range nodes {
			count++
			expression(n.Condition)
			expression(n.Requires)
		}
	}
	add(p.Root)
	for _, b := range p.Blocks {
		add(b.Nodes)
	}
	return count
}

// StructuralCandidates never mutates the input. The limit bounds cloning cost;
// executable state and independent simulation, not a text heuristic, rank edits.
func (p *Program) StructuralCandidates(limit int) []StructuralVariant {
	limit = min(max(limit, 0), 128)
	out := []StructuralVariant{}
	names := []string{"$root"}
	blocks := []string{}
	for name := range p.Blocks {
		blocks = append(blocks, name)
	}
	sort.Strings(blocks)
	names = append(names, blocks...)
	add := func(block string, nodes []Node, edit StructuralEdit) {
		if len(out) >= limit || len(nodes) == 0 {
			return
		}
		next := *p
		next.Blocks = make(map[string]Block, len(p.Blocks))
		for key, value := range p.Blocks {
			next.Blocks[key] = value
		}
		if block == "$root" {
			next.Root = nodes
		} else {
			b := next.Blocks[block]
			b.Nodes = nodes
			next.Blocks[block] = b
		}
		if next.Validate() == nil {
			out = append(out, StructuralVariant{&next, edit})
		}
	}
	for _, block := range names {
		nodes := p.Root
		if block != "$root" {
			b := p.Blocks[block]
			if b.Macro != "" {
				continue
			}
			nodes = b.Nodes
		}
		for i, n := range nodes {
			if len(out) >= limit {
				return out
			}
			if i+1 < len(nodes) && p.canSwap(n, nodes[i+1]) {
				next := append([]Node(nil), nodes...)
				next[i], next[i+1] = next[i+1], next[i]
				add(block, next, StructuralEdit{"swap", block, n.ID, nodes[i+1].ID})
			}
			redundantCheck := n.Kind == "check" && i > 0 && nodes[i-1].Kind == "check"
			optionalOutput := (n.Kind == "burst" || n.Kind == "skill") && n.Options["required"] != "true" && len(n.Options) == 0
			if !optionalOutput && (n.Kind == "burst" || n.Kind == "skill") && n.Options["required"] != "true" {
				optionalOutput = true
				for key := range n.Options {
					if key != "if" && key != "fast" {
						optionalOutput = false
					}
				}
			}
			if redundantCheck || optionalOutput {
				next := append([]Node(nil), nodes[:i]...)
				next = append(next, nodes[i+1:]...)
				add(block, next, StructuralEdit{"drop", block, n.ID, ""})
			}
			if n.Kind == "branch" && n.Options["then"] != "" && n.Options["then"] == n.Options["else"] && n.Options["then"] == n.Options["unknown"] {
				next := append([]Node(nil), nodes...)
				changed := n
				changed.Kind = "call"
				changed.Condition = nil
				changed.Args = []string{n.Options["then"]}
				changed.Options = map[string]string{}
				if n.Options["required"] == "true" {
					changed.Options["required"] = "true"
				}
				next[i] = changed
				add(block, next, StructuralEdit{"collapse_branch", block, n.ID, ""})
			}
		}
	}
	return out
}
func (p *Program) canSwap(a, b Node) bool {
	if a.Options["once"] != "" || b.Options["once"] != "" {
		return false
	}
	ar, aw := p.recordEffects(a)
	br, bw := p.recordEffects(b)
	for key := range aw {
		if br[key] || bw[key] {
			return false
		}
	}
	for key := range bw {
		if ar[key] {
			return false
		}
	}
	return true
}
func (p *Program) recordEffects(n Node) (map[string]bool, map[string]bool) {
	reads, writes, visited := map[string]bool{}, map[string]bool{}, map[string]bool{}
	var expr func(*Expression)
	expr = func(e *Expression) {
		if e == nil {
			return
		}
		if e.Name == "record-active" || e.Name == "record-exists" || e.Name == "record-remaining" {
			reads[e.Argument] = true
		}
		expr(e.Left)
		expr(e.Right)
	}
	var visit func(Node)
	visit = func(n Node) {
		for _, key := range []string{"keep", "maintain", "watch", "refresh"} {
			if value := n.Options[key]; value != "" {
				reads[value] = true
			}
		}
		for _, key := range []string{"record", "refresh"} {
			if value := n.Options[key]; value != "" {
				writes[value] = true
			}
		}
		expr(n.Condition)
		expr(n.Requires)
		for _, name := range targets(n) {
			if visited[name] {
				continue
			}
			visited[name] = true
			b := p.Blocks[name]
			visit(b.Declaration)
			for _, child := range b.Nodes {
				visit(child)
			}
		}
	}
	visit(n)
	return reads, writes
}
