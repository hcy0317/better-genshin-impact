package nativeflow

type Truth int

const (
	Unknown Truth = -1
	False   Truth = 0
	True    Truth = 1
)

func truth(value bool) Truth {
	if value {
		return True
	}
	return False
}
func (v Truth) String() string {
	switch v {
	case True:
		return "then"
	case False:
		return "else"
	default:
		return "unknown"
	}
}
func (e *Expression) Evaluate(observe func(string, string) Truth) Truth {
	return e.EvaluateWithValues(observe, nil)
}

func (e *Expression) EvaluateWithValues(observe func(string, string) Truth, number func(string, string) (float64, bool)) Truth {
	if e == nil {
		return True
	}
	switch e.Op {
	case "gt", "ge", "lt", "le", "eq", "ne":
		if number == nil {
			return Unknown
		}
		value, known := number(e.Name, e.Argument)
		if !known {
			return Unknown
		}
		switch e.Op {
		case "gt":
			return truth(value > e.Threshold)
		case "ge":
			return truth(value >= e.Threshold)
		case "lt":
			return truth(value < e.Threshold)
		case "le":
			return truth(value <= e.Threshold)
		case "eq":
			return truth(value == e.Threshold)
		default:
			return truth(value != e.Threshold)
		}
	case "call":
		return observe(e.Name, e.Argument)
	case "not":
		v := e.Left.EvaluateWithValues(observe, number)
		if v == Unknown {
			return Unknown
		}
		return truth(v == False)
	case "and":
		a := e.Left.EvaluateWithValues(observe, number)
		if a == False {
			return False
		}
		b := e.Right.EvaluateWithValues(observe, number)
		if b == False {
			return False
		}
		if a == Unknown || b == Unknown {
			return Unknown
		}
		return True
	case "or":
		a := e.Left.EvaluateWithValues(observe, number)
		if a == True {
			return True
		}
		b := e.Right.EvaluateWithValues(observe, number)
		if b == True {
			return True
		}
		if a == Unknown || b == Unknown {
			return Unknown
		}
		return False
	}
	return Unknown
}
