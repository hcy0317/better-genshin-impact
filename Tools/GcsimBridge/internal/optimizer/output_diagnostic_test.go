package optimizer_test

import (
 "bytes"
 "context"
 "encoding/json"
 "os"
 "regexp"
 "testing"
 "time"

 "github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
)

func TestOriginalOutputSizeDiagnostic(t *testing.T) {
 input,binary:=os.Getenv("BGI_ORIGINAL_OPTIMIZER_REQUEST"),os.Getenv("BGI_OUTPUT_DIAGNOSTIC_BINARY")
 if input==""||binary==""{t.Skip("optional bounded original-request diagnostic")}
 data,err:=os.ReadFile(input);if err!=nil{t.Fatal(err)}
 started:=time.Now()
 result,runErr:=host.RunWorker(context.Background(),binary,"--optimizer-worker",data,host.Limits{WallTime:120*time.Second,MemoryBytes:768<<20,OutputBytes:16<<20})
 t.Logf("diagnostic only: elapsed=%s error=%v stdout=%d stderr=%d",time.Since(started),runErr,len(result.Stdout),len(result.Stderr))
 statuses:=regexp.MustCompile(`"status":"([a-z_]+)"`).FindAllSubmatch(result.Stdout,3)
 for _,status:=range statuses{t.Logf("reported status: %s",status[1])}
 marker:=[]byte(`"samples":[`)
 if at:=bytes.Index(result.Stdout,marker);at>=0{
  var sample map[string]json.RawMessage
  if err:=json.NewDecoder(bytes.NewReader(result.Stdout[at+len(marker):])).Decode(&sample);err==nil{
   sizes:=map[string]int{};for name,value:=range sample{sizes[name]=len(value)}
   encoded,_:=json.Marshal(sizes);t.Logf("first sample field bytes (no user values): %s",encoded)
  }
 }
}
