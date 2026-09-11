package optimizer_test

import (
 "encoding/json"
 "os"
 "reflect"
 "slices"
 "testing"
 "time"

 "github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
 "github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
)

func TestOriginalThreeScenariosCompactStatsAreLossless(t *testing.T) {
 file:=os.Getenv("BGI_ORIGINAL_OPTIMIZER_REQUEST")
 if file==""{t.Skip("optional private request equivalence test")}
 data,err:=os.ReadFile(file);if err!=nil{t.Fatal(err)}
 var input optimizer.Request;if err=json.Unmarshal(data,&input);err!=nil{t.Fatal(err)}
 if len(input.Items)!=1145||len(input.Scenarios)!=3||len(input.ValidationSeeds)!=1000{t.Fatal("wrong original replay")}
 items:=map[int]engine.Artifact{};for _,item:=range input.Items{items[item.ScanIndex]=item.Artifact}
 for _,scenario:=range input.Scenarios {
  t.Run(scenario.ID,func(t *testing.T){
   r:=scenario.Evaluation;r.SchemaVersion="1";r.EngineRevision=engine.Revision;r.Seeds=input.ValidationSeeds[:8];r.CompactSamples=false
   r.Inventory=&input.Inventory;r.Equipment=map[string][]engine.Artifact{}
   assign:=func(name string,ids []int){for _,id:=range ids{item,ok:=items[id];if !ok{t.Fatal("missing item")};r.Equipment[name]=append(r.Equipment[name],item)}}
   for name,ids:=range scenario.FixedEquipment{assign(name,ids)}
   for _,c:=range input.Characters{if len(scenario.Participants)==0||slices.Contains(scenario.Participants,c.Character){assign(c.Character,c.Current)}}
   started:=time.Now();full,err:=engine.Evaluate(r);if err!=nil{t.Fatal(err)};fullTime:=time.Since(started)
   r.CompactSamples=true;started=time.Now();compact,err:=engine.Evaluate(r);if err!=nil{t.Fatal(err)};compactTime:=time.Since(started)
   for name,pair:=range map[string][2]any{
    "DPS":{full.MeanDPS,compact.MeanDPS},"seed DPS":{full.ScoredDPS,compact.ScoredDPS},"metrics":{full.Metrics,compact.Metrics},
    "validation":{full.Validation,compact.Validation},"rounds":{full.RoundTraces,compact.RoundTraces},"energy":{full.EnergyWindows,compact.EnergyWindows},
    "stats":{full.InitialStats,compact.InitialStats},"warnings":{full.Warnings,compact.Warnings},"issues":{full.MetricIssues,compact.MetricIssues},
    "buffs":{full.BuffActivations,compact.BuffActivations},"assumptions":{full.Assumptions,compact.Assumptions},
   }{if !reflect.DeepEqual(pair[0],pair[1]){t.Fatal(name+" changed")}}
   for i,sample:=range full.Samples{
    actual:=compact.Samples[i]
    if sample.Seed!=actual.Seed||sample.Duration!=actual.Duration||sample.DPS!=actual.DPS||sample.TotalDamage!=actual.TotalDamage||sample.TargetOverlap!=actual.TargetOverlap||!reflect.DeepEqual(sample.EndStats,actual.EndStats){t.Fatal("retained sample fields changed")}
    for j,c:=range sample.Characters{a:=actual.Characters[j];if c.Name!=a.Name||c.ActiveTime!=a.ActiveTime||c.EnergySpent!=a.EnergySpent{t.Fatal("retained character fields changed")}}
   }
   t.Logf("8-seed diagnostic only: full=%s compact=%s; all retained evidence equal",fullTime,compactTime)
  })
 }
}
