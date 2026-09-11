package sdkoverlay_test

import (
	"bytes"
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
	"time"
)

func TestActualBridgeCarriesVerifiedOverlayIdentity(t *testing.T) {
	if os.Getenv("BGI_SANDRONE_BUILD_TEST") != "1" {
		t.Skip("explicit isolated production-bridge build probe")
	}
	root, err := filepath.Abs("../..")
	if err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(t.TempDir(), "candidate")
	ctx, cancel := context.WithTimeout(context.Background(), 240*time.Second)
	defer cancel()
	build := exec.CommandContext(ctx, "go", "run", "./cmd/build-sandrone", "-output", output)
	build.Dir = root
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("candidate build: %v\n%s", err, out)
	}
	name := "gcsim-bridge"
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	executable := filepath.Join(output, runtime.GOOS, name)
	capCommand := exec.CommandContext(ctx, executable, "--capabilities")
	data, err := capCommand.Output()
	if err != nil {
		t.Fatal(err)
	}
	var caps struct {
		EngineRevision     string                                                `json:"engineRevision"`
		SDK                struct{ Source, ModelRevision, OverlaySHA256 string } `json:"sdk"`
		KnownCharacterKeys []string                                              `json:"knownCharacterKeys"`
	}
	if err = json.Unmarshal(data, &caps); err != nil {
		t.Fatal(err)
	}
	if caps.SDK.Source != "verified_vendor_overlay" || caps.SDK.ModelRevision != "sandrone-model-v1" || len(caps.SDK.OverlaySHA256) != 64 {
		t.Fatalf("missing actual identity: %s", data)
	}
	found := false
	for _, key := range caps.KnownCharacterKeys {
		found = found || key == "sandrone"
	}
	if !found {
		t.Fatal("actual bridge lacks character")
	}
	catalogData, err := exec.CommandContext(ctx, executable, "--catalog").Output()
	if err != nil {
		t.Fatal(err)
	}
	var catalog struct {
		Characters []struct {
			ID  int    `json:"id"`
			Key string `json:"key"`
		} `json:"characters"`
		Localization struct {
			CharacterNames map[string]string `json:"character_names"`
		} `json:"localization"`
	}
	if err = json.Unmarshal(catalogData, &catalog); err != nil {
		t.Fatal(err)
	}
	found = false
	for _, actor := range catalog.Characters {
		if actor.Key == "sandrone" && actor.ID == 10000133 {
			found = true
		}
	}
	if !found || catalog.Localization.CharacterNames["sandrone"] != "桑多涅" {
		t.Fatal("catalog lacks exact inventory identity or Chinese name")
	}
	for _, cons := range []int{0, 6} {
		config := `options duration=12 workers=1 iteration=1;
target lvl=90 resist=0.1;
lisa char lvl=90/90 cons=0 talent=6,6,6;
lisa add weapon="apprenticesnotes" refine=1 lvl=70/70;
sandrone char lvl=90/90 cons=CONS talent=10,10,10;
sandrone add weapon="wastergreatsword" refine=1 lvl=70/70;
active lisa; lisa skill; sandrone charge; wait(500);`
		config = string(bytes.ReplaceAll([]byte(config), []byte("CONS"), []byte{byte('0' + cons)}))
		request := map[string]any{"evaluation": map[string]any{"schemaVersion": "1", "engineRevision": caps.EngineRevision, "config": config, "seeds": []int{17}, "allowPartial": true}, "limits": map[string]any{"wallTimeMs": 15000, "memoryMiB": 768, "outputKiB": 16384}}
		input, _ := json.Marshal(request)
		command := exec.CommandContext(ctx, executable)
		command.Stdin = bytes.NewReader(input)
		data, err := command.Output()
		if err != nil {
			t.Fatalf("C%d actual bridge: %v %s", cons, err, data)
		}
		var result struct {
			Status string
			Report struct {
				MeanDPS float64 `json:"meanDps"`
				Support string
			}
		}
		if err = json.Unmarshal(data, &result); err != nil {
			t.Fatal(err)
		}
		if result.Status != "completed" || result.Report.MeanDPS <= 0 || result.Report.Support != "trial" {
			t.Fatalf("C%d invalid or falsely complete report: %s", cons, data)
		}
	}
}
