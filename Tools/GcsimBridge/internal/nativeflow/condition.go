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
	if e == nil {
		return True
	}
	switch e.Op {
	case "call":
		return observe(e.Name, e.Argument)
	case "not":
		v := e.Left.Evaluate(observe)
		if v == Unknown {
			return Unknown
		}
		return truth(v == False)
	case "and":
		a := e.Left.Evaluate(observe)
		if a == False {
			return False
		}
		b := e.Right.Evaluate(observe)
		if b == False {
			return False
		}
		if a == Unknown || b == Unknown {
			return Unknown
		}
		return True
	case "or":
		a := e.Left.Evaluate(observe)
		if a == True {
			return True
		}
		b := e.Right.Evaluate(observe)
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
