package nativeflow

import (
	"encoding/json"
	"testing"
)

func TestNineStrategySyntaxRetainsExtendedConditionsAndActions(t *testing.T) {
	var p Program
	err := json.Unmarshal([]byte(`{"schemaVersion":"native-flow-v1","source":"fixture","root":[
 {"id":"e","kind":"skill","character":"sangonomiyakokomi","options":{"record":"water"}},
 {"id":"q","kind":"burst","character":"sangonomiyakokomi","options":{"refresh":"water"}},
 {"id":"charge","kind":"charge","character":"nahida","seconds":0.3},
 {"id":"dash","kind":"dash","character":"kamisatoayaka","seconds":0.2},
 {"id":"walk","kind":"walk","character":"xiangling","seconds":0.15,"args":["w","0.15"]},
 {"id":"a","kind":"attack","character":"nahida","seconds":0.4,"condition":{"op":"and","left":{"op":"call","name":"round-odd"},"right":{"op":"gt","name":"record-remaining","argument":"water","threshold":3}}}
 ]}`), &p)
	if err != nil {
		t.Fatal(err)
	}
	if err = p.Validate(); err != nil {
		t.Fatal(err)
	}
}

func TestRecordTimeComparisonDoesNotTurnUnknownIntoAFalseObservation(t *testing.T) {
	e := &Expression{Op: "gt", Name: "record-remaining", Argument: "shield", Threshold: 3}
	observe := func(string, string) Truth { return Unknown }
	if got := e.EvaluateWithValues(observe, func(string, string) (float64, bool) { return 100, false }); got != Unknown {
		t.Fatal(got)
	}
	if got := e.EvaluateWithValues(observe, func(string, string) (float64, bool) { return 3, true }); got != False {
		t.Fatal(got)
	}
	if got := e.EvaluateWithValues(observe, func(string, string) (float64, bool) { return 3.01, true }); got != True {
		t.Fatal(got)
	}
}
