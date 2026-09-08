package engine

import (
	"encoding/json"
	"testing"
)

func TestCatalogShipsChineseNamesFromPinnedUpstream(t *testing.T) {
	data, _ := json.Marshal(Catalog())
	var actual struct {
		Localization struct {
			Characters map[string]string `json:"character_names"`
			Weapons    map[string]string `json:"weapon_names"`
			Sets       map[string]string `json:"artifact_names"`
		} `json:"localization"`
	}
	if err := json.Unmarshal(data, &actual); err != nil {
		t.Fatal(err)
	}
	if actual.Localization.Characters["raidenshogun"] != "雷电将军" {
		t.Fatalf("missing Chinese character: %v", actual.Localization.Characters["raidenshogun"])
	}
	if actual.Localization.Weapons["engulfinglightning"] != "薙草之稻光" {
		t.Fatal("missing Chinese weapon")
	}
	if actual.Localization.Sets["emblemofseveredfate"] != "绝缘之旗印" {
		t.Fatal("missing Chinese set")
	}
	if actual.Localization.Characters["aetherelectro"] != "空 (雷元素)" {
		t.Fatal("traveler element override missing")
	}
}
