package engine

import "github.com/genshinsim/gcsim/pkg/core/action"

// The simulation owns NextAction; never stop the SDK evaluator from its own
// logging goroutine. This also detects finite scripts that end before N rounds.
type exhaustionObserver struct {
	action.Evaluator
	exhausted bool
}

func (e *exhaustionObserver) NextAction() (*action.Eval, error) {
	next, err := e.Evaluator.NextAction()
	if next == nil && err == nil {
		e.exhausted = true
	}
	return next, err
}
