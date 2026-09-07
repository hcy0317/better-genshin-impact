package optimizer

import (
	"context"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

type Item struct {
	engine.Artifact
	Location    string `json:"location"`
	Locked      bool   `json:"locked"`
	Fingerprint string `json:"fingerprint"`
}

type Character struct {
	CharacterGoal
	Current      []int               `json:"current"`
	Protected    bool                `json:"protected"`
	FixedSlots   map[string]int      `json:"fixedSlots"`
	MainStats    map[string][]string `json:"mainStats"`
	RequiredSets map[string]int      `json:"requiredSets"`
	MinimumStats map[string]float64  `json:"minimumStats"`
	ProxyWeights map[string]float64  `json:"proxyWeights"`
}

type Scenario struct {
	ID             string           `json:"id"`
	Weight         float64          `json:"weight"`
	Evaluation     engine.Request   `json:"evaluation"`
	FixedEquipment map[string][]int `json:"fixedEquipment"`
	Participants   []string         `json:"participants"`
}

type Request struct {
	SchemaVersion       string              `json:"schemaVersion"`
	Mode                string              `json:"mode"`
	Inventory           engine.InventoryRef `json:"inventory"`
	Items               []Item              `json:"items"`
	Characters          []Character         `json:"characters"`
	ProtectedCharacters []string            `json:"protectedCharacters"`
	Scenarios           []Scenario          `json:"scenarios"`
	SearchSeeds         []int64             `json:"searchSeeds"`
	ValidationSeeds     []int64             `json:"validationSeeds"`
	EvaluationBudget    int                 `json:"evaluationBudget"`
	Exact               bool                `json:"exact"`
}

type Plan struct {
	Equipment     map[string][]int         `json:"equipment"`
	Rank          Rank                     `json:"rank"`
	Reports       map[string]engine.Report `json:"reports"`
	Qualified     bool                     `json:"qualified"`
	Indeterminate bool                     `json:"indeterminate"`
	Issues        []string                 `json:"issues"`
}

type Impact struct {
	Character string `json:"character"`
	Before    []int  `json:"before"`
	After     []int  `json:"after"`
}
type Result struct {
	Improvement     *Improvement      `json:"improvement,omitempty"`
	Status          string            `json:"status"`
	Plan            *Plan             `json:"plan,omitempty"`
	Baseline        *Plan             `json:"baseline,omitempty"`
	Impacts         []Impact          `json:"impacts"`
	Evaluations     int               `json:"evaluations"`
	Exhaustive      bool              `json:"exhaustive"`
	Message         string            `json:"message"`
	ScenarioAliases map[string]string `json:"scenarioAliases"`
	ReferenceGoals  []CharacterGoal   `json:"referenceGoals"`
	ReferenceSource string            `json:"referenceSource"`
}

type Evaluator func(context.Context, engine.Request) (engine.Report, error)
