// Package sdkoverlay prepares build-only character extensions in an isolated
// vendor tree. It never modifies the module cache or activates engine features.
package sdkoverlay

import (
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"go/format"
	"os"
	"path/filepath"
	"strings"
)

const SDKVersion = "v1.15.2-0.20260907234712-720f1a1f8167"

func Sandrone(vendor, output string, sources map[string][]byte) (string, error) {
	modules, err := os.ReadFile(filepath.Join(vendor, "modules.txt"))
	if err != nil {
		return "", err
	}
	count := 0
	for _, line := range strings.Split(strings.ReplaceAll(string(modules), "\r\n", "\n"), "\n") {
		if strings.HasPrefix(line, "# github.com/genshinsim/gcsim ") {
			if line != "# github.com/genshinsim/gcsim "+SDKVersion {
				return "", fmt.Errorf("unsupported or replaced gcsim vendor identity: %s", line)
			}
			count++
		}
	}
	if count != 1 {
		return "", fmt.Errorf("expected exactly one pinned gcsim vendor module")
	}
	const existingPackage = "github.com/genshinsim/gcsim/internal/characters/prune\n"
	moduleText := strings.ReplaceAll(string(modules), "\r\n", "\n")
	if strings.Count(moduleText, existingPackage) != 1 || strings.Contains(moduleText, "github.com/genshinsim/gcsim/internal/characters/sandrone") {
		return "", fmt.Errorf("unexpected vendored character package list")
	}
	moduleText = strings.Replace(moduleText, existingPackage, existingPackage+"github.com/genshinsim/gcsim/internal/characters/sandrone\n", 1)
	if len(sources) == 0 {
		return "", fmt.Errorf("no Sandrone source files")
	}
	for name := range sources {
		if filepath.Base(name) != name || strings.ContainsAny(name, "/\\:") || !strings.HasSuffix(name, ".go") {
			return "", fmt.Errorf("invalid character source filename %q", name)
		}
	}
	vendor, err = filepath.Abs(vendor)
	if err != nil {
		return "", err
	}
	sdk := filepath.Join(vendor, "github.com/genshinsim/gcsim")
	files := []struct{ path, hash string }{
		{"pkg/core/keys/character.dm.go", "7bc7aa19ae026aaefffbb32fbf1f88ddbc5e4861590678cb86163d94b8cac8fe"},
		{"pkg/simulation/imports.character.dm.go", "655ce419c5467bf246b0cd8b576e942cd397e014f20f9d2000728404e62b66df"},
		{"pkg/reactable/stellarconduct_gadget.go", "9ceb095f4bcc22739dba3fa72569655231fd3ea6db78ebd91eaef161c45e3ecd"},
		{"pkg/reactable/stellarswirl_gadget.go", "d29cda200530382aabd325c404caa2937cde31d6c1feb16e30646a6152a55b53"},
		{"pkg/reactable/stellarswirl.go", "44b377b9833dd532082190fe299e39a73b92b303c1142c4c6deca000d37bbfe7"},
	}
	patched := map[string][]byte{}
	for _, file := range files {
		path := filepath.Join(sdk, filepath.FromSlash(file.path))
		b, e := os.ReadFile(path)
		if e != nil {
			return "", e
		}
		if fmt.Sprintf("%x", sha256.Sum256(b)) != file.hash {
			return "", fmt.Errorf("unsupported SDK source layout or content: %s", file.path)
		}
		patched[path] = b
	}
	keyPath := filepath.Join(sdk, "pkg/core/keys/character.dm.go")
	k := string(patched[keyPath])
	k = strings.Replace(k, "\tInvalidChar                   // invalidchar", "\tSandrone                      // sandrone\n\tInvalidChar                   // invalidchar", 1)
	k = strings.Replace(k, "\t\"invalidchar\",", "\t\"sandrone\",\n\t\"invalidchar\",", 1)
	k = strings.Replace(k, "\tInvalidChar,", "\tSandrone,\n\tInvalidChar,", 1)
	patched[keyPath] = []byte(k)
	importPath := filepath.Join(sdk, "pkg/simulation/imports.character.dm.go")
	patched[importPath] = []byte(strings.Replace(string(patched[importPath]), "import (", "import (\n\t_ \"github.com/genshinsim/gcsim/internal/characters/sandrone\"", 1))
	fieldPath := filepath.Join(sdk, "pkg/reactable/stellarconduct_gadget.go")
	field := string(patched[fieldPath])
	if strings.Count(field, "args[1].(*info.AttackInfo)") != 1 {
		return "", fmt.Errorf("unexpected stellar field event assertion")
	}
	// The pinned event producer emits an AttackInfo value, not a pointer.
	patched[fieldPath] = []byte(strings.Replace(field, "args[1].(*info.AttackInfo)", "args[1].(info.AttackInfo)", 1))
	vortexPath := filepath.Join(sdk, "pkg/reactable/stellarswirl_gadget.go")
	vortex := string(patched[vortexPath])
	if strings.Count(vortex, "info.GadgetTypPolestarField") != 1 {
		return "", fmt.Errorf("unexpected stellar vortex gadget type")
	}
	// nearbySSwVortex searches this type to reuse the live vortex and its stacks.
	patched[vortexPath] = []byte(strings.Replace(vortex, "info.GadgetTypPolestarField", "info.GadgetTypStellarVortex", 1))
	swirlPath := filepath.Join(sdk, "pkg/reactable/stellarswirl.go")
	swirl := string(patched[swirlPath])
	const stackUpdate = "r.core.Flags.Custom[sswStackKey] += min(r.core.Flags.Custom[sswStackKey]+1, sswMaxStacks)"
	if strings.Count(swirl, stackUpdate) != 1 {
		return "", fmt.Errorf("unexpected stellar swirl stack update")
	}
	patched[swirlPath] = []byte(strings.Replace(swirl, stackUpdate, strings.Replace(stackUpdate, "+=", "=", 1), 1))
	for name, source := range sources {
		patched[filepath.Join(sdk, "internal/characters/sandrone", name)] = source
	}
	// Complete all validation before creating an output. Unique child directories
	// avoid overwriting other builds or input files, including on failed retries.
	for path, b := range patched {
		formatted, e := format.Source(b)
		if e != nil {
			return "", fmt.Errorf("invalid overlay source %s: %w", path, e)
		}
		patched[path] = formatted
	}
	out, err := os.MkdirTemp(output, "sandrone-overlay-")
	if err != nil {
		return "", err
	}
	out, err = filepath.Abs(out)
	if err != nil {
		return "", err
	}
	replacements := map[string]string{}
	for target, b := range patched {
		rel, e := filepath.Rel(sdk, target)
		if e != nil {
			return "", e
		}
		backing := filepath.Join(out, "sources", rel)
		if e := os.MkdirAll(filepath.Dir(backing), 0700); e != nil {
			return "", e
		}
		if e := os.WriteFile(backing, b, 0600); e != nil {
			return "", e
		}
		replacements[target] = backing
	}
	moduleBacking := filepath.Join(out, "modules.txt")
	if err := os.WriteFile(moduleBacking, []byte(moduleText), 0600); err != nil {
		return "", err
	}
	// Go reads vendor/modules.txt outside its overlay filesystem. The caller
	// must copy this candidate only into its disposable build vendor directory.
	manifest, err := json.Marshal(struct{ Replace map[string]string }{replacements})
	if err != nil {
		return "", err
	}
	path := filepath.Join(out, "overlay.json")
	if err := os.WriteFile(path, manifest, 0600); err != nil {
		return "", err
	}
	return path, nil
}
