// build-sandrone creates an isolated, explicitly incomplete character candidate.
// It never changes the module cache, package pointer, or an installed engine.
package main

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"flag"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sandroneassets"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sdkoverlay"
)

const checksum = "h1:Rt/2gr0JK/o/a2Q/cv2AJarJd0XLma6W02mRkMI/n0k="
const revision = "720f1a1f81673f9dc82f803e32239c4ad729bd0c"
const modelRevision = "sandrone-model-v1"

func main() {
	output := flag.String("output", "", "new candidate directory (must not already exist)")
	flag.Parse()
	if err := build(*output); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func build(output string) error {
	if output == "" {
		return fmt.Errorf("explicit new output directory required")
	}
	root, err := os.Getwd()
	if err != nil {
		return err
	}
	output, err = filepath.Abs(output)
	if err != nil {
		return err
	}
	if _, err = os.Lstat(output); !os.IsNotExist(err) {
		return fmt.Errorf("candidate output must not already exist: %s", output)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 220*time.Second)
	defer cancel()
	run := func(dir, platform string, args ...string) ([]byte, error) {
		cmd := exec.CommandContext(ctx, "go", args...)
		cmd.Dir = dir
		env := os.Environ()
		// Override, rather than duplicate, inherited build settings.
		for _, entry := range []string{"GOWORK=off", "GOPROXY=off", "GOFLAGS=", "CGO_ENABLED=0", "GOMAXPROCS=2", "GOARCH=amd64", "GOOS=" + platform} {
			key := strings.SplitN(entry, "=", 2)[0] + "="
			filtered := env[:0]
			for _, old := range env {
				if !strings.HasPrefix(old, key) {
					filtered = append(filtered, old)
				}
			}
			env = append(filtered, entry)
		}
		cmd.Env = env
		data, e := cmd.CombinedOutput()
		if e != nil {
			return nil, fmt.Errorf("go %v: %w\n%s", args, e, data)
		}
		return data, nil
	}
	if _, err = run(root, "windows", "mod", "verify"); err != nil {
		return err
	}
	metadata, err := run(root, "windows", "list", "-m", "-json", "github.com/genshinsim/gcsim")
	if err != nil {
		return err
	}
	var sdk struct {
		Dir, Path, Version, Sum string
		Replace                 any
	}
	if err = json.Unmarshal(metadata, &sdk); err != nil {
		return err
	}
	if sdk.Path != "github.com/genshinsim/gcsim" || sdk.Version != sdkoverlay.SDKVersion || sdk.Sum != checksum || sdk.Replace != nil {
		return fmt.Errorf("SDK is not the pinned checksummed module")
	}
	work, err := os.MkdirTemp("", "sandrone-candidate-")
	if err != nil {
		return err
	}
	fmt.Fprintln(os.Stderr, "Isolated build directory:", work)
	for _, name := range []string{"go.mod", "go.sum", "cmd", "internal"} {
		if err = copySource(filepath.Join(root, name), filepath.Join(work, name)); err != nil {
			return err
		}
	}
	vendor := filepath.Join(work, "vendor")
	if _, err = run(root, "windows", "mod", "vendor", "-o", vendor); err != nil {
		return err
	}
	sourceDigest, err := sdkoverlay.VerifyVendorSource(filepath.Join(vendor, "github.com/genshinsim/gcsim"), sdk.Dir)
	if err != nil {
		return err
	}
	sources := sandroneassets.Sources()
	power, err := os.ReadFile(filepath.Join(root, "internal/sandronemodel/power.go"))
	if err != nil {
		return err
	}
	sources["power.go"] = []byte(strings.Replace(string(power), "package sandronemodel", "package sandrone", 1))
	overlay, err := sdkoverlay.Sandrone(vendor, work, sources)
	if err != nil {
		return err
	}
	modules, err := os.ReadFile(filepath.Join(filepath.Dir(overlay), "modules.txt"))
	if err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(vendor, "modules.txt"), modules, 0600); err != nil {
		return err
	}
	raw, err := os.ReadFile(overlay)
	if err != nil {
		return err
	}
	var mapping struct{ Replace map[string]string }
	if err = json.Unmarshal(raw, &mapping); err != nil {
		return err
	}
	targets := make([]string, 0, len(mapping.Replace))
	for target := range mapping.Replace {
		targets = append(targets, target)
	}
	sort.Strings(targets)
	digest := sha256.New()
	for _, target := range targets {
		rel, e := filepath.Rel(vendor, target)
		if e != nil || strings.HasPrefix(rel, "..") {
			return fmt.Errorf("overlay target outside isolated vendor")
		}
		data, e := os.ReadFile(mapping.Replace[target])
		if e != nil {
			return e
		}
		fmt.Fprintf(digest, "%s\x00%x\n", filepath.ToSlash(rel), sha256.Sum256(data))
	}
	overlayDigest := fmt.Sprintf("%x", digest.Sum(nil))
	record := map[string]any{"schemaVersion": 1, "module": sdk.Path, "version": sdk.Version, "checksum": sdk.Sum, "revision": revision, "modelRevision": modelRevision, "overlaySHA256": overlayDigest}
	raw, err = json.Marshal(record)
	if err != nil {
		return err
	}
	backing := filepath.Join(work, "sdk-build-record.go.txt")
	if err = os.WriteFile(backing, []byte("package main\nvar vendorBuildRecord = "+strconv.Quote(string(raw))+"\n"), 0600); err != nil {
		return err
	}
	mapping.Replace[filepath.Join(work, "cmd/gcsim-bridge/sdk_build.go")] = backing
	raw, err = json.Marshal(mapping)
	if err != nil {
		return err
	}
	if err = os.WriteFile(overlay, raw, 0600); err != nil {
		return err
	}
	if err = os.MkdirAll(filepath.Join(vendor, "github.com/genshinsim/gcsim/internal/characters/sandrone"), 0700); err != nil {
		return err
	}
	if err = os.Mkdir(output, 0700); err != nil {
		return err
	}
	for _, platform := range []string{"windows", "linux"} {
		dir := filepath.Join(output, platform)
		if err = os.Mkdir(dir, 0700); err != nil {
			return err
		}
		name := "gcsim-bridge"
		if platform == "windows" {
			name += ".exe"
		}
		binary := filepath.Join(dir, name)
		flags := "-X github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine.SupplementalCharacterRevision=" + modelRevision
		if _, err = run(work, platform, "build", "-p", "2", "-buildvcs=false", "-trimpath", "-mod=vendor", "-overlay="+overlay, "-ldflags", flags, "-o", binary, "./cmd/gcsim-bridge"); err != nil {
			return err
		}
		data, e := os.ReadFile(binary)
		if e != nil {
			return e
		}
		manifest := map[string]any{"schemaVersion": 1, "platform": platform, "architecture": "amd64", "executable": name, "engineRevision": revision, "adapterVersion": "3", "sdk": record, "sha256": fmt.Sprintf("%x", sha256.Sum256(data)), "complete": false, "status": "experimental_incomplete_explicit_trial_only", "vendorSourceSHA256": sourceDigest}
		raw, e = json.MarshalIndent(manifest, "", "  ")
		if e != nil {
			return e
		}
		if err = os.WriteFile(filepath.Join(dir, "manifest.json"), raw, 0600); err != nil {
			return err
		}
	}
	fmt.Println("Candidate only; active installation unchanged:", output)
	return nil
}

func copySource(source, destination string) error {
	return filepath.WalkDir(source, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.Type()&os.ModeSymlink != 0 {
			return fmt.Errorf("linked build source rejected: %s", path)
		}
		rel, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		target := filepath.Join(destination, rel)
		if entry.IsDir() {
			return os.MkdirAll(target, 0700)
		}
		if !entry.Type().IsRegular() {
			return fmt.Errorf("nonregular build source: %s", path)
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		return os.WriteFile(target, data, 0600)
	})
}
