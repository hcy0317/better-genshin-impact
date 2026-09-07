package engine

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"strconv"
	"sync"

	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/glog"
	"github.com/genshinsim/gcsim/pkg/gcs/ast"
	"github.com/genshinsim/gcsim/pkg/gcs/parser"
	"github.com/genshinsim/gcsim/pkg/stats"
)

type LoopChoice struct {
	Index int `json:"index"`
	Line  int `json:"line"`
}
type ObservedRound struct {
	Round
	Complete        bool               `json:"complete"`
	DurationSeconds float64            `json:"durationSeconds"`
	StartEnergy     map[string]float64 `json:"startEnergy"`
	EndEnergy       map[string]float64 `json:"endEnergy,omitempty"`
}
type RoundTrace struct {
	Seed    string          `json:"seed"`
	State   string          `json:"state"`
	Rounds  []ObservedRound `json:"rounds"`
	Choices []LoopChoice    `json:"choices,omitempty"`
	Issues  []string        `json:"issues,omitempty"`
}
type roundObserver struct {
	glog.Logger
	core             *core.Core
	mu               sync.Mutex
	trace            RoundTrace
	active           *ObservedRound
	startTag, endTag string
	onStart          func()
	linear           bool
}

func containsOuterExit(node ast.Node) bool {
	switch n := node.(type) {
	case *ast.CtrlStmt, *ast.ReturnStmt, *ast.SwitchStmt:
		return true
	case *ast.BlockStmt:
		for _, child := range n.List {
			if containsOuterExit(child) {
				return true
			}
		}
	case *ast.IfStmt:
		return containsOuterExit(n.IfBlock) || (n.ElseBlock != nil && containsOuterExit(n.ElseBlock))
		// Nested loop control does not delimit a main-loop iteration. Functions are
		// separate scopes; their returns are not returns from the selected loop.
	}
	return false
}

func observeAutomaticRounds(script ast.Node, file *ast.File, c *core.Core, seed int64, index, offset int) (*ast.BlockStmt, *roundObserver, error) {
	program, ok := script.Copy().(*ast.BlockStmt)
	if !ok {
		return nil, nil, errors.New("gcsim did not produce a statement block")
	}
	observer := &roundObserver{Logger: c.Log, core: c, trace: RoundTrace{Seed: strconv.FormatInt(seed, 10), State: "complete", Rounds: []ObservedRound{}}}
	var bodies []*ast.BlockStmt
	for _, node := range program.List {
		var body *ast.BlockStmt
		switch n := node.(type) {
		case *ast.ForStmt:
			body = n.Body
		case *ast.WhileStmt:
			body = n.WhileBlock
		}
		if body != nil {
			bodies = append(bodies, body)
			observer.trace.Choices = append(observer.trace.Choices, LoopChoice{Index: len(bodies), Line: max(1, file.Position(node.Position()).Line-offset)})
		}
	}
	if len(bodies) == 0 {
		if index != 0 {
			observer.trace.State = "ambiguous"
			observer.trace.Issues = []string{"所选主循环已不存在，请重新选择"}
			return program, observer, nil
		}
		observer.linear = true
		return program, observer, nil
	}
	if (index == 0 && len(bodies) > 1) || index < 0 || index > len(bodies) {
		observer.trace.State = "ambiguous"
		observer.trace.Issues = []string{"存在多个主循环或所选循环已改变，请选择需要统计的循环位置"}
		return program, observer, nil
	}
	if index == 0 {
		index = 1
	}
	body := bodies[index-1]
	if containsOuterExit(body) {
		observer.trace.State = "unavailable"
		observer.trace.Issues = []string{"主循环含跳出、返回或复杂分支，无法可靠自动划分完整轮次；请调整或单独选择主循环"}
		return program, observer, nil
	}
	// Capture the native print function before user declarations. Random private
	// names/tags keep an untrusted script's own logging from forging boundaries.
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		return nil, nil, err
	}
	key := hex.EncodeToString(nonce)
	name := "__bgi_observe_" + key
	observer.startTag = "bgi-start-" + key
	observer.endTag = "bgi-end-" + key
	_, markerNode, err := parser.New(ast.NewFile(), fmt.Sprintf("let %s = print; %s(%q); %s(%q);", name, name, observer.startTag, name, observer.endTag)).Parse()
	markers, ok := markerNode.(*ast.BlockStmt)
	if err != nil || !ok || len(markers.List) != 3 {
		return nil, nil, errors.New("unable to prepare trusted round markers")
	}
	body.List = append([]ast.Node{markers.List[1]}, append(body.List, markers.List[2])...)
	program.List = append([]ast.Node{markers.List[0]}, program.List...)
	c.Log = observer
	return program, observer, nil
}

func (o *roundObserver) energy() map[string]float64 {
	values := map[string]float64{}
	for _, c := range o.core.Player.Chars() {
		values[c.Base.Key.String()] = c.Energy
	}
	return values
}
func (o *roundObserver) NewEvent(message string, source glog.Source, character int) glog.Event {
	if source == glog.LogUserEvent && (message == o.startTag || message == o.endTag) {
		o.mu.Lock()
		if len(o.trace.Rounds) >= 128 {
			o.trace.State = "unavailable"
			o.trace.Issues = []string{"自动轮次超过128轮，未完整记录"}
			o.active = nil
		} else if message == o.startTag {
			if o.active != nil {
				o.trace.State = "unavailable"
				o.trace.Issues = []string{"循环边界不完整"}
			}
			o.active = &ObservedRound{Round: Round{ID: fmt.Sprintf("round-%d", len(o.trace.Rounds)+1), StartFrame: o.core.F}, StartEnergy: o.energy()}
		} else if o.active != nil {
			value := *o.active
			value.EndFrame = o.core.F
			value.Complete = value.EndFrame > value.StartFrame
			value.DurationSeconds = float64(value.EndFrame-value.StartFrame) / 60
			value.EndEnergy = o.energy()
			if !value.Complete {
				o.trace.State = "unavailable"
				o.trace.Issues = []string{"循环没有推进模拟时间"}
			}
			o.trace.Rounds = append(o.trace.Rounds, value)
			o.active = nil
		}
		start := message == o.startTag
		o.mu.Unlock()
		if start && o.onStart != nil {
			o.onStart()
		}
	}
	return o.Logger.NewEvent(message, source, character)
}
func (o *roundObserver) finish(duration int) RoundTrace {
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.linear {
		o.trace.Rounds = []ObservedRound{{Round: Round{ID: "whole-script", EndFrame: duration}, Complete: duration > 0, DurationSeconds: float64(duration) / 60}}
	}
	if o.active != nil {
		value := *o.active
		value.EndFrame = duration
		value.DurationSeconds = float64(duration-value.StartFrame) / 60
		value.EndEnergy = o.energy()
		o.trace.Rounds = append(o.trace.Rounds, value)
		o.trace.State = "partial"
		o.trace.Issues = append(o.trace.Issues, "最后一轮在模拟终止时尚未完成，不作为已完成轮次")
	}
	if len(o.trace.Rounds) == 0 && o.trace.State == "complete" {
		o.trace.State = "unavailable"
		o.trace.Issues = []string{"脚本没有完成可统计的循环"}
	}
	return o.trace
}
func traceWindows(trace RoundTrace, warmup int) ([]Round, error) {
	if trace.State != "complete" || warmup < 0 || warmup >= len(trace.Rounds) {
		return nil, errors.New("自动轮次不完整、无法确定或忽略开场后没有完整轮次")
	}
	rounds := make([]Round, 0, len(trace.Rounds)-warmup)
	for _, r := range trace.Rounds[warmup:] {
		if !r.Complete {
			return nil, errors.New("存在未完成轮次")
		}
		rounds = append(rounds, r.Round)
	}
	return rounds, nil
}

// Equal weight belongs to independent samples, not the number of completed
// loops in each sample. Normalize only after these raw metrics are aggregated.
func SummarizeAutomaticMetrics(samples []stats.Result, windows [][]Round) ([]Metric, error) {
	if len(samples) == 0 || len(samples) != len(windows) {
		return nil, errors.New("automatic metric batch is incomplete")
	}
	var total []Metric
	for i, sample := range samples {
		if len(windows[i]) == 0 {
			return nil, errors.New("a declared sample has no complete scoring round")
		}
		values, err := SummarizeMetrics([]stats.Result{sample}, windows[i])
		if err != nil {
			return nil, err
		}
		if i == 0 {
			total = make([]Metric, len(values))
			copy(total, values)
			for j := range total {
				total[j].Value = 0
				total[j].Aggregation = "sample_round_mean_then_sample_mean"
			}
		}
		if len(values) != len(total) {
			return nil, errors.New("automatic metric identities differ")
		}
		for j, value := range values {
			if value.Character != total[j].Character || value.Kind != total[j].Kind {
				return nil, errors.New("automatic metric identities differ")
			}
			total[j].Value += value.Value / float64(len(samples))
		}
	}
	return total, nil
}
