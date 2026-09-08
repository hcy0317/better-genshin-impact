package nativeflow

import (
	"fmt"
	"math"
	"sort"

	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/action"
	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/player/character"
)

// All mutable simulation state is owned by NextAction / simulator callbacks.
// Start is intentionally empty: the SDK calls it in a goroutine.
type Evaluator struct {
	program                   *Program
	core                      *core.Core
	stack                     []*frame
	pending                   *pendingAction
	once                      map[string]bool
	records                   map[string]record
	watches                   map[string]Node
	maintenanceAttempts       map[string]int
	started, finished, closed bool
	failure                   error
	Events                    []TraceEvent
	TraceTruncated            bool
	OnRound                   func(start, complete bool)
}
type frame struct {
	name                          string
	declaration                   Node
	nodes                         []Node
	index, deadline               int
	ok, admitted, recoveryHandled bool
	caller                        *Node
	watch                         string
	watchDemand                   int
}
type pendingAction struct {
	node                        Node
	deadline, until, inputFrame int
	sent, executed              bool
	feeding                     bool
}
type record struct {
	at, expiry int
	known      bool
}
type TraceEvent struct {
	Frame  int    `json:"frame"`
	Node   string `json:"node"`
	Kind   string `json:"kind"`
	Detail string `json:"detail"`
}

func (e *Evaluator) trace(n Node, kind, detail string) {
	if len(e.Events) < 128 {
		e.Events = append(e.Events, TraceEvent{e.core.F, n.ID, kind, detail})
	} else {
		e.TraceTruncated = true
	}
}
func New(p *Program, c *core.Core) (*Evaluator, error) {
	if err := p.Validate(); err != nil {
		return nil, err
	}
	e := &Evaluator{program: p, core: c, once: map[string]bool{}, records: map[string]record{}, watches: map[string]Node{}, maintenanceAttempts: map[string]int{}, Events: []TraceEvent{}}
	c.Events.Subscribe(event.OnActionExec, func(args ...any) {
		pending := e.pending
		if pending == nil || !pending.sent || pending.feeding || args[1].(action.Action) != actionKind(pending.node.Kind) || c.Player.ByIndex(args[0].(int)).Base.Key.String() != pending.node.Character {
			return
		}
		pending.executed = true
		pending.inputFrame = c.F
		if pending.node.Kind == "attack" && pending.until < 0 {
			pending.until = c.F + frames(pending.node.Seconds)
		}
		e.trace(pending.node, "action", pending.node.Kind)
		if pending.node.Kind == "skill" || pending.node.Kind == "burst" {
			e.recordAction(pending.node, c.F)
		}
	}, "bettergi/native-flow-success")
	return e, nil
}
func (e *Evaluator) Start()      {}
func (e *Evaluator) Continue()   {}
func (e *Evaluator) Exit() error { e.closed = true; return e.failure }
func (e *Evaluator) Err() error  { return e.failure }
func (e *Evaluator) wait() *action.Eval {
	return &action.Eval{Action: action.ActionWait, Param: map[string]int{"f": 1}}
}
func frames(seconds float64) int { return int(math.Round(seconds * 60)) }
func actionKind(kind string) action.Action {
	switch kind {
	case "skill":
		return action.ActionSkill
	case "burst":
		return action.ActionBurst
	case "charge":
		return action.ActionCharge
	default:
		return action.ActionAttack
	}
}
func (e *Evaluator) char(name string) *character.CharWrapper {
	for _, c := range e.core.Player.Chars() {
		if c.Base.Key.String() == name {
			return c
		}
	}
	return nil
}
func (e *Evaluator) fail(n Node, message string) (*action.Eval, error) {
	e.failure = n.Error(message)
	return nil, e.failure
}
func (e *Evaluator) newRoot() {
	e.stack = append(e.stack, &frame{name: "$root", nodes: e.program.Root, deadline: math.MaxInt32, ok: true, admitted: true, declaration: Node{ID: "root"}})
	if e.OnRound != nil {
		e.OnRound(true, true)
	}
}
func (e *Evaluator) push(name string, caller *Node, watch string, recovery bool) error {
	if len(e.stack) >= 32 {
		return fmt.Errorf("native_flow: 调用深度超过32")
	}
	b := e.program.Blocks[name]
	deadline := math.MaxInt32
	if !recovery && len(e.stack) > 0 {
		deadline = e.stack[len(e.stack)-1].deadline
	}
	if recovery || watch != "" {
		deadline = min(deadline, e.core.F+15*60)
	}
	if raw := b.Declaration.Options["timeout"]; raw != "" {
		deadline = min(deadline, e.core.F+frames(option(b.Declaration.Options, "timeout", 15)))
	} else if b.Declaration.Options["atomic"] == "true" {
		deadline = min(deadline, e.core.F+8*60)
	}
	if caller != nil && (caller.Options["timeout"] != "" || caller.Options["once"] != "") {
		deadline = min(deadline, e.core.F+frames(option(caller.Options, "timeout", 15)))
	}
	nodes := b.Nodes
	if b.Macro == "neuvillette_charge_v1" {
		n := b.Nodes[0]
		n.Kind = "charge"
		n.Options = map[string]string{"required": "true", "keep": n.Options["keep"]}
		nodes = []Node{n}
	}
	e.stack = append(e.stack, &frame{name: name, declaration: b.Declaration, nodes: nodes, deadline: deadline, ok: true, caller: caller, watch: watch})
	e.trace(b.Declaration, "call", name)
	return nil
}
func (e *Evaluator) finishFrame() {
	f := e.stack[len(e.stack)-1]
	if f.watch != "" {
		left, known := e.remaining(f.watch)
		f.ok = f.ok && known && left > f.watchDemand
	}
	e.stack = e.stack[:len(e.stack)-1]
	if f.ok {
		if name := f.declaration.Options["record"]; name != "" {
			e.records[name] = record{at: e.core.F}
			e.trace(f.declaration, "record", name)
		}
		if f.caller != nil && f.caller.Options["once"] == "battle" {
			e.once[f.name] = true
		}
	}
	e.trace(f.declaration, "return", fmt.Sprintf("%s:%t", f.name, f.ok))
	if len(e.stack) == 0 {
		if e.OnRound != nil {
			e.OnRound(false, f.ok)
		}
		if !e.program.Loop {
			e.finished = true
		}
	} else if !f.ok && (f.watch != "" || f.caller != nil && f.caller.Options["required"] == "true") {
		e.stack[len(e.stack)-1].ok = false
	}
}
func (e *Evaluator) completeAction(ok bool) {
	p := e.pending
	e.pending = nil
	if p == nil {
		return
	}
	if !ok {
		e.trace(p.node, "failed", "条件、等待或动作预算未满足")
		if p.node.Options["required"] == "true" && len(e.stack) > 0 {
			e.stack[len(e.stack)-1].ok = false
		}
	} else if p.node.Options["watch"] == "" && p.node.Options["maintain"] == "" {
		clear(e.maintenanceAttempts)
	}
}
func (e *Evaluator) NextAction() (*action.Eval, error) {
	if e.closed {
		return nil, e.failure
	}
	if e.finished {
		return nil, nil
	}
	if !e.started {
		e.started = true
		e.newRoot()
	}
	for transitions := 0; transitions < 128; transitions++ {
		if len(e.stack) == 0 {
			if e.finished {
				return nil, nil
			}
			e.newRoot()
			return e.wait(), nil
		}
		if e.pending != nil {
			return e.advanceAction()
		}
		if e.maybeWatch() {
			continue
		}
		f := e.stack[len(e.stack)-1]
		if !f.admitted {
			f.admitted = true
			horizon := 0
			if f.declaration.Options["atomic"] == "true" {
				horizon = e.estimate(f.name) + 15
			}
			if e.evaluate(f.declaration.Requires, horizon) != True {
				f.ok = false
			}
			if horizon > 0 {
				for _, n := range e.program.Blocks[f.name].Nodes {
					if keep := n.Options["keep"]; keep != "" {
						left, known := e.remaining(keep)
						if !known || left <= horizon {
							f.ok = false
						}
					}
				}
			}
		}
		if e.core.F >= f.deadline || e.evaluate(f.declaration.Requires, 0) != True {
			f.ok = false
		}
		if !f.ok || f.index == len(f.nodes) {
			if !f.ok && !f.recoveryHandled && f.declaration.Options["onfail"] != "" {
				f.recoveryHandled = true
				if err := e.push(f.declaration.Options["onfail"], nil, "", true); err != nil {
					return nil, err
				}
				continue
			}
			e.finishFrame()
			continue
		}
		n := f.nodes[f.index]
		f.index++
		if n.Kind == "branch" {
			choice := e.evaluate(n.Condition, 0).String()
			target := n.Options[choice]
			e.trace(n, "branch", choice+":"+target)
			if target != "" {
				if err := e.push(target, &n, "", false); err != nil {
					return nil, err
				}
			} else if n.Options["required"] == "true" {
				f.ok = false
			}
			continue
		}
		if n.Condition != nil && e.evaluate(n.Condition, 0) != True {
			e.trace(n, "skipped", "条件否定或未知")
			if n.Options["required"] == "true" {
				f.ok = false
			}
			continue
		}
		if n.Kind == "call" {
			target := n.Args[0]
			if n.Options["once"] == "battle" && e.once[target] {
				e.trace(n, "satisfied", "once:"+target)
				continue
			}
			if err := e.push(target, &n, "", false); err != nil {
				return nil, err
			}
			continue
		}
		if n.Kind == "check" {
			e.trace(n, "check", "终止由gcsim敌人/场景状态决定")
			continue
		}
		if keep := n.Options["keep"]; keep != "" {
			demand := e.coverage(n)
			if f.declaration.Options["atomic"] == "true" {
				demand = max(demand, e.estimate(f.name)+15)
			}
			left, known := e.remaining(keep)
			if !known || left <= demand {
				e.trace(n, "skipped", "keep窗口不足："+keep)
				if n.Options["required"] == "true" {
					f.ok = false
				}
				continue
			}
		}
		if name := n.Options["maintain"]; name != "" {
			demand := max(frames(option(n.Options, "before", 0)), e.nextCoverage(f, name))
			for _, active := range e.stack {
				if active.watch == name {
					demand = max(demand, active.watchDemand)
				}
			}
			left, known := e.remaining(name)
			if known && left > demand {
				if n.Options["watch"] != "" {
					e.watches[name] = n
				}
				e.trace(n, "satisfied", "maintain:"+name)
				continue
			}
		}
		timeout := max(8.0, float64(e.coverage(n))/60)
		if n.Kind == "skill" && n.Options["wait"] == "true" {
			timeout = max(timeout, option(e.program.Timings[n.Options["timing"]], "cd", 15)+float64(e.coverage(n))/60)
		}
		deadline := min(f.deadline, e.core.F+frames(option(n.Options, "timeout", timeout)))
		e.pending = &pendingAction{node: n, deadline: deadline, until: -1}
	}
	return e.fail(Node{}, "无时间进展的控制转移超过128次")
}
func (e *Evaluator) advanceAction() (*action.Eval, error) {
	p := e.pending
	n := p.node
	valid := e.core.F < p.deadline
	for _, f := range e.stack {
		if f.ok && (e.core.F >= f.deadline || e.evaluate(f.declaration.Requires, 0) != True) {
			valid = false
		}
	}
	if keep := n.Options["keep"]; keep != "" {
		left, known := e.remaining(keep)
		valid = valid && known && left > 0
	}
	if !valid {
		if p.executed && !p.feeding && !e.actionComplete(n.Kind) {
			return e.fail(n, "native_flow_indeterminate：已开始的动作无法等价中断；没有返回截断或完整命中的合格结果")
		}
		e.completeAction(false)
		return e.wait(), nil
	}
	target := n.Character
	if p.feeding {
		target = n.Options["feed"]
	}
	c := e.char(target)
	if c == nil {
		return e.fail(n, "动作角色不在当前模拟队伍："+target)
	}
	if e.core.Player.ActiveChar().Base.Key != c.Base.Key {
		if err := e.core.Player.ReadyCheck(action.ActionSwap, c.Base.Key, nil); err != nil {
			return e.wait(), nil
		}
		return &action.Eval{Char: c.Base.Key, Action: action.ActionSwap, Param: map[string]int{}}, nil
	}
	if n.Kind == "wait" || p.feeding {
		if p.until < 0 {
			seconds := n.Seconds
			if p.feeding {
				seconds = 0.8
			}
			p.until = e.core.F + frames(seconds)
		}
		if e.core.F >= p.until {
			e.completeAction(true)
		}
		return e.wait(), nil
	}
	if p.executed {
		if !e.actionComplete(n.Kind) {
			return e.wait(), nil
		}
		if n.Kind != "attack" || e.core.F >= p.until {
			if n.Options["feed"] != "" {
				p.feeding = true
				p.until = -1
				return e.wait(), nil
			}
			e.completeAction(true)
			return e.wait(), nil
		}
		p.executed = false
		p.sent = false
	}
	if n.Kind == "skill" && n.Options["fast"] == "true" && n.Options["wait"] != "true" && c.Cooldown(action.ActionSkill) > 0 {
		e.completeAction(false)
		return e.wait(), nil
	}
	kind := actionKind(n.Kind)
	params := map[string]int{}
	if n.Options["hold"] == "true" {
		params["hold"] = 1
	}
	if err := e.core.Player.ReadyCheck(kind, c.Base.Key, params); err != nil {
		return e.wait(), nil
	}
	p.sent = true
	return &action.Eval{Char: c.Base.Key, Action: kind, Param: params}, nil
}
func (e *Evaluator) actionComplete(kind string) bool {
	if kind == "charge" {
		return e.core.Player.SwapCD < math.MaxInt16 && !e.core.Player.IsAnimationLocked(action.ActionSwap)
	}
	return !e.core.Player.IsAnimationLocked(action.ActionWait)
}
func (e *Evaluator) recordAction(n Node, at int) {
	name := n.Options["record"]
	if name == "" {
		return
	}
	r := record{at: at}
	if timing, ok := e.program.Timings[n.Options["timing"]]; ok && timing["duration"] != "" {
		r.known = true
		r.expiry = at + frames(option(timing, "duration", 0))
	} else if n.Character == "furina" && n.Kind == "skill" {
		// The pinned SDK sets this status at the successful skill call.
		c := e.char(n.Character)
		if c.StatusIsActive("furina-skill") {
			r.known = true
			r.expiry = c.StatusExpiry("furina-skill")
		}
	}
	e.records[name] = r
	e.trace(n, "record", name)
	if n.Options["watch"] != "" {
		e.watches[name] = n
	}
}
func (e *Evaluator) remaining(name string) (int, bool) {
	r, ok := e.records[name]
	if !ok {
		return 0, true
	}
	if !r.known {
		return 0, false
	}
	return r.expiry - e.core.F, true
}
func (e *Evaluator) evaluate(expr *Expression, horizon int) Truth {
	return expr.Evaluate(func(name, argument string) Truth {
		switch name {
		case "record-exists":
			_, ok := e.records[argument]
			return truth(ok)
		case "record-active":
			left, known := e.remaining(argument)
			if !known {
				return Unknown
			}
			return truth(left > horizon)
		}
		c := e.char(argument)
		if c == nil {
			return Unknown
		}
		switch name {
		case "q-ready":
			ok, _ := c.ActionReady(action.ActionBurst, nil)
			return truth(ok)
		case "q-energy-low":
			return truth(c.Energy < c.EnergyMax)
		case "q-cd":
			return truth(c.Cooldown(action.ActionBurst) > 0)
		case "e-ready":
			ok, _ := c.ActionReady(action.ActionSkill, nil)
			return truth(ok)
		case "e-cd":
			return truth(c.Cooldown(action.ActionSkill) > 0)
		}
		// Visual low-hp cannot be derived from an unconfigured image threshold.
		return Unknown
	})
}
func (e *Evaluator) coverage(n Node) int { return actionFrames(n) + 60 + 15 }
func actionFrames(n Node) int {
	switch n.Kind {
	case "skill":
		v := 60
		if n.Options["hold"] == "true" {
			v = 120
		}
		if n.Options["feed"] != "" {
			v += 108
		}
		return v
	case "burst":
		return 120
	case "wait", "attack":
		return frames(n.Seconds)
	case "call", "branch", "segment":
		return 0
	default:
		return 15
	}
}
func (e *Evaluator) estimate(name string) int {
	b := e.program.Blocks[name]
	total := 0
	actor := ""
	for _, n := range b.Nodes {
		if n.Kind == "call" {
			total += e.estimate(n.Args[0])
			actor = ""
			continue
		}
		if n.Kind == "branch" {
			largest := 0
			for _, k := range []string{"then", "else", "unknown"} {
				if target := n.Options[k]; target != "" {
					largest = max(largest, e.estimate(target))
				}
			}
			total += largest
			actor = ""
			continue
		}
		if actor != n.Character {
			total += 60
			actor = n.Character
		}
		total += actionFrames(n)
	}
	return total
}
func (e *Evaluator) nextCoverage(f *frame, name string) int {
	for _, n := range f.nodes[f.index:] {
		if n.Kind == "call" {
			b := e.program.Blocks[n.Args[0]]
			if b.Declaration.Options["atomic"] == "true" {
				for _, v := range b.Nodes {
					if v.Options["keep"] == name {
						return e.estimate(n.Args[0]) + 15
					}
				}
			}
			return e.nextCoverage(&frame{nodes: b.Nodes}, name)
		}
		if n.Options["keep"] == name {
			return e.coverage(n)
		}
		return 0
	}
	return 0
}
func (e *Evaluator) maybeWatch() bool {
	if len(e.watches) == 0 {
		return false
	}
	for _, f := range e.stack {
		if !f.ok || f.declaration.Options["atomic"] == "true" || f.watch != "" || f.caller != nil && f.caller.Options["once"] == "battle" {
			return false
		}
	}
	f := e.stack[len(e.stack)-1]
	names := make([]string, 0, len(e.watches))
	for name := range e.watches {
		names = append(names, name)
	}
	sort.Slice(names, func(i, j int) bool {
		a, _ := e.remaining(names[i])
		b, _ := e.remaining(names[j])
		if a == b {
			return names[i] < names[j]
		}
		return a < b
	})
	for _, name := range names {
		n := e.watches[name]
		left, known := e.remaining(name)
		demand := max(frames(option(n.Options, "before", 0)), e.nextCoverage(f, name))
		if !known || left > demand {
			continue
		}
		if e.maintenanceAttempts[name] >= 3 {
			f.ok = false
			e.trace(n, "maintenance_failed", "维护没有取得有效进展："+name)
			return false
		}
		e.maintenanceAttempts[name]++
		e.trace(n, "maintenance", name)
		if err := e.push(n.Options["watch-target"], nil, name, false); err != nil {
			e.failure = err
			f.ok = false
			return false
		}
		e.stack[len(e.stack)-1].watchDemand = demand
		return true
	}
	return false
}
