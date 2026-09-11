package sdkoverlay_test

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/genshinsim/gcsim/pkg/core/keys"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sandroneassets"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sdkoverlay"
)

// This opt-in probe builds the real vendored SDK with a new internal package.
// The probe deliberately registers NO character factory or production capability.
func TestVendorOverlayCompilesNewInternalPackage(t *testing.T) {
	if os.Getenv("BGI_SDK_OVERLAY_TEST") != "1" {
		t.Skip("set BGI_SDK_OVERLAY_TEST=1 for isolated SDK build probe")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Second)
	defer cancel()
	root, err := filepath.Abs("../..")
	if err != nil {
		t.Fatal(err)
	}
	work := t.TempDir()
	run := func(dir string, args ...string) []byte {
		t.Helper()
		cmd := exec.CommandContext(ctx, "go", args...)
		cmd.Dir = dir
		cmd.Env = append(os.Environ(), "GOWORK=off", "GOPROXY=off", "GOTOOLCHAIN=local", "GOFLAGS=", "GOOS="+runtime.GOOS, "GOARCH="+runtime.GOARCH, "GOMAXPROCS=2")
		out, e := cmd.CombinedOutput()
		if e != nil {
			t.Fatalf("go %v: %v\n%s", args, e, out)
		}
		return out
	}
	for _, name := range []string{"go.mod", "go.sum"} {
		b, e := os.ReadFile(filepath.Join(root, name))
		if e != nil {
			t.Fatal(e)
		}
		if e = os.WriteFile(filepath.Join(work, name), b, 0600); e != nil {
			t.Fatal(e)
		}
	}
	var sdk struct{ Dir string }
	if e := json.Unmarshal(run(root, "list", "-m", "-json", "github.com/genshinsim/gcsim"), &sdk); e != nil {
		t.Fatal(e)
	}
	vendor := filepath.Join(work, "vendor")
	run(root, "mod", "vendor", "-o", vendor)
	paths := []string{"pkg/core/keys/character.dm.go", "pkg/simulation/imports.character.dm.go", "pkg/reactable/stellarconduct_gadget.go", "pkg/reactable/stellarswirl_gadget.go", "pkg/reactable/stellarswirl.go"}
	before := map[string][]byte{}
	for _, p := range paths {
		for _, base := range []string{sdk.Dir, filepath.Join(vendor, "github.com/genshinsim/gcsim")} {
			full := filepath.Join(base, p)
			b, e := os.ReadFile(full)
			if e != nil {
				t.Fatal(e)
			}
			before[full] = b
		}
	}
	probe := []byte("package sandrone\nimport tmpl \"github.com/genshinsim/gcsim/internal/template/character\"\nvar _ *tmpl.Character\n")
	sources := map[string][]byte{"probe.go": probe}
	modelTest := os.Getenv("BGI_SANDRONE_MODEL_TEST") == "1"
	if modelTest {
		sources = sandroneassets.Sources()
		b, e := os.ReadFile(filepath.Join(root, "internal/sandronemodel/power.go"))
		if e != nil {
			t.Fatal(e)
		}
		sources["power.go"] = []byte(strings.Replace(string(b), "package sandronemodel", "package sandrone", 1))
	}
	overlay, e := sdkoverlay.Sandrone(vendor, t.TempDir(), sources)
	if e != nil {
		t.Fatal(e)
	}
	originalModules, e := os.ReadFile(filepath.Join(vendor, "modules.txt"))
	if e != nil {
		t.Fatal(e)
	}
	patchedModules, e := os.ReadFile(filepath.Join(filepath.Dir(overlay), "modules.txt"))
	if e != nil {
		t.Fatal(e)
	}
	line := "github.com/genshinsim/gcsim/internal/characters/sandrone\n"
	if strings.Count(string(patchedModules), line) != 1 || strings.Replace(string(patchedModules), line, "", 1) != strings.ReplaceAll(string(originalModules), "\r\n", "\n") {
		t.Fatal("vendor module patch changed more than one package entry")
	}
	// Only this test-owned, newly created vendor copy is changed.
	if e := os.WriteFile(filepath.Join(vendor, "modules.txt"), patchedModules, 0600); e != nil {
		t.Fatal(e)
	}
	if modelTest {
		// go test/vet needs a real working directory even for virtual source files.
		if e := os.MkdirAll(filepath.Join(vendor, "github.com/genshinsim/gcsim/internal/characters/sandrone"), 0700); e != nil {
			t.Fatal(e)
		}
		t.Log(string(run(work, "test", "-p", "2", "-mod=vendor", "-overlay="+overlay, "github.com/genshinsim/gcsim/internal/characters/sandrone", "-count=1", "-v")))
	}
	main := `package main
import (
 "fmt"
 "runtime/debug"
 "github.com/genshinsim/gcsim/pkg/core/keys"
 _ "github.com/genshinsim/gcsim/pkg/simulation"
)
func main() {
 for _, k := range keys.CharValues() { fmt.Printf("key:%d:%s\n", int(k), k.String()) }
 i,_ := debug.ReadBuildInfo(); for _, d := range i.Deps { if d.Path=="github.com/genshinsim/gcsim" { fmt.Printf("sdk:%s:sum=%q:replacement=%t\n",d.Version,d.Sum,d.Replace!=nil) } }
}
`
	if e := os.WriteFile(filepath.Join(work, "main.go"), []byte(main), 0600); e != nil {
		t.Fatal(e)
	}
	bin := filepath.Join(work, "probe.exe")
	run(work, "build", "-buildvcs=false", "-p", "2", "-mod=vendor", "-overlay="+overlay, "-o", bin, ".")
	out, e := exec.CommandContext(ctx, bin).CombinedOutput()
	if e != nil {
		t.Fatalf("probe: %v %s", e, out)
	}
	for _, k := range keys.CharValues() {
		if k == keys.InvalidChar {
			continue
		}
		want := []byte(fmt.Sprintf("key:%d:%s\n", int(k), k.String()))
		if !bytes.Contains(out, want) {
			t.Fatalf("existing key changed: %s\n%s", want, out)
		}
	}
	if !bytes.Contains(out, []byte(fmt.Sprintf("key:%d:sandrone\n", int(keys.InvalidChar)))) {
		t.Fatalf("new identity missing: %s", out)
	}
	if !bytes.Contains(out, []byte(fmt.Sprintf("key:%d:invalidchar\n", int(keys.InvalidChar)+1))) {
		t.Fatalf("sentinel misplaced: %s", out)
	}
	if !strings.Contains(string(out), "sdk:"+sdkoverlay.SDKVersion) || !strings.Contains(string(out), "replacement=false") {
		t.Fatalf("SDK identity changed: %s", out)
	}
	t.Logf("Isolated probe SDK metadata: %s", strings.Split(string(out), "sdk:")[1])
	for full, b := range before {
		after, e := os.ReadFile(full)
		if e != nil || !bytes.Equal(b, after) {
			t.Fatalf("source modified: %s (%v)", full, e)
		}
	}
	// Restore the package list so these cases reach the source-integrity gate,
	// rather than passing because the already-patched package list is rejected.
	if e := os.WriteFile(filepath.Join(vendor, "modules.txt"), originalModules, 0600); e != nil {
		t.Fatal(e)
	}
	for _, name := range []string{"stellarconduct_gadget.go", "stellarswirl_gadget.go", "stellarswirl.go"} {
		t.Run("reject altered "+name, func(t *testing.T) {
			field := filepath.Join(vendor, "github.com/genshinsim/gcsim/pkg/reactable", name)
			t.Cleanup(func() {
				if e := os.WriteFile(field, before[field], 0600); e != nil {
					t.Error(e)
				}
			})
			if e := os.WriteFile(field, append(bytes.Clone(before[field]), '\n'), 0600); e != nil {
				t.Fatal(e)
			}
			out := t.TempDir()
			if _, e := sdkoverlay.Sandrone(vendor, out, sources); e == nil || !strings.Contains(e.Error(), "unsupported SDK source layout or content: pkg/reactable/"+name) {
				t.Fatalf("expected changed-source rejection for %s, got %v", name, e)
			}
			entries, e := os.ReadDir(out)
			if e != nil || len(entries) != 0 {
				t.Fatalf("rejected source wrote output: %v %v", entries, e)
			}
		})
	}
}
