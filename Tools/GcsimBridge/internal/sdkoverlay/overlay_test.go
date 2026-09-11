package sdkoverlay_test

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sdkoverlay"
)

func TestRejectUnpinnedVendor(t *testing.T) {
	vendor := t.TempDir()
	if err := os.WriteFile(filepath.Join(vendor, "modules.txt"), []byte("# github.com/genshinsim/gcsim v0.0.0\n"), 0600); err != nil {
		t.Fatal(err)
	}
	_, err := sdkoverlay.Sandrone(vendor, t.TempDir(), map[string][]byte{"probe.go": []byte("package sandrone\n")})
	if err == nil {
		t.Fatal("unknown SDK must not receive a character patch")
	}
}

func TestRejectUnsafeSourcesAndChangedSDKWithoutWritingOutput(t *testing.T) {
	for _, tc := range []struct{ name, source string }{
		{"parent traversal", "../probe.go"}, {"Windows traversal", `..\probe.go`},
		{"absolute path", `C:\probe.go`}, {"not Go", "probe.txt"}, {"altered SDK", "probe.go"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			vendor, out := t.TempDir(), t.TempDir()
			modules := "# github.com/genshinsim/gcsim " + sdkoverlay.SDKVersion + "\n## explicit; go 1.24\ngithub.com/genshinsim/gcsim/internal/characters/prune\n"
			if e := os.WriteFile(filepath.Join(vendor, "modules.txt"), []byte(modules), 0600); e != nil {
				t.Fatal(e)
			}
			key := filepath.Join(vendor, "github.com/genshinsim/gcsim/pkg/core/keys/character.dm.go")
			if e := os.MkdirAll(filepath.Dir(key), 0700); e != nil {
				t.Fatal(e)
			}
			if e := os.WriteFile(key, []byte("package keys\n"), 0600); e != nil {
				t.Fatal(e)
			}
			if _, e := sdkoverlay.Sandrone(vendor, out, map[string][]byte{tc.source: []byte("package sandrone\n")}); e == nil {
				t.Fatal("invalid source accepted")
			}
			files, e := os.ReadDir(out)
			if e != nil || len(files) != 0 {
				t.Fatalf("failure wrote output: %v %v", files, e)
			}
		})
	}
}

func TestRejectReplacementOrDuplicateNativeCharacter(t *testing.T) {
	for _, modules := range []string{
		"# github.com/genshinsim/gcsim " + sdkoverlay.SDKVersion + " => ./local\n",
		"# github.com/genshinsim/gcsim " + sdkoverlay.SDKVersion + "\ngithub.com/genshinsim/gcsim/internal/characters/prune\ngithub.com/genshinsim/gcsim/internal/characters/sandrone\n",
	} {
		vendor := t.TempDir()
		if e := os.WriteFile(filepath.Join(vendor, "modules.txt"), []byte(modules), 0600); e != nil {
			t.Fatal(e)
		}
		if _, e := sdkoverlay.Sandrone(vendor, t.TempDir(), map[string][]byte{"probe.go": []byte("package sandrone\n")}); e == nil {
			t.Fatal("replacement/native character must not be patched")
		}
	}
}
