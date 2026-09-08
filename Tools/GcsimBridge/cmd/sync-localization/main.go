// Generates the Chinese display catalog from the resolved gcsim module, not a
// separately maintained translation database. Run again whenever the SDK changes.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
)

func main() {
	moduleDir := flag.String("module-dir", "", "Resolved gcsim module directory")
	output := flag.String("output", "internal/engine/names.zh.json", "Generated Chinese catalog")
	flag.Parse()
	merged := map[string]map[string]string{}
	for _, file := range []string{"names.dm.json", "names.override.json"} {
		data, err := os.ReadFile(filepath.Join(*moduleDir, "ui", "packages", "localization", "src", "locales", file))
		check(err)
		var document map[string]map[string]map[string]string
		check(json.Unmarshal(data, &document))
		for _, category := range []string{"character_names", "weapon_names", "artifact_names"} {
			if merged[category] == nil {
				merged[category] = map[string]string{}
			}
			for key, value := range document["Chinese"][category] {
				merged[category][key] = value
			}
		}
	}
	for _, category := range []string{"character_names", "weapon_names", "artifact_names"} {
		if len(merged[category]) == 0 {
			check(fmt.Errorf("upstream Chinese %s is missing", category))
		}
	}
	data, err := json.MarshalIndent(merged, "", "  ")
	check(err)
	check(os.WriteFile(*output, append(data, '\n'), 0644))
}
func check(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
