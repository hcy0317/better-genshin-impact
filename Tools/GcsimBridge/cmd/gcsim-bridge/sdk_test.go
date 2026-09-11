package main

import (
	"runtime/debug"
	"strings"
	"testing"
)

func TestVendoredSDKNeedsVerifiedBuildRecord(t *testing.T) {
	dep := &debug.Module{Path: "github.com/genshinsim/gcsim", Version: expectedSDKVersion}
	if _, err := validateSDKDependency(dep, ""); err == nil {
		t.Fatal("unproven vendor accepted")
	}
	record := `{"schemaVersion":1,"module":"github.com/genshinsim/gcsim","version":"` + expectedSDKVersion + `","checksum":"h1:Rt/2gr0JK/o/a2Q/cv2AJarJd0XLma6W02mRkMI/n0k=","revision":"720f1a1f81673f9dc82f803e32239c4ad729bd0c","modelRevision":"sandrone-model-v1","overlaySHA256":"` + strings.Repeat("a", 64) + `"}`
	id, err := validateSDKDependency(dep, record)
	if err != nil {
		t.Fatal(err)
	}
	if id.Source != "verified_vendor_overlay" || id.OverlaySHA256 != strings.Repeat("a", 64) {
		t.Fatalf("identity not disclosed: %+v", id)
	}
	for _, bad := range []string{strings.Replace(record, "Rt/2", "AAAA", 1), strings.Replace(record, "720f1a1f", "00000000", 1), strings.Replace(record, "sandrone-model-v1", "unknown", 1), strings.Replace(record, strings.Repeat("a", 64), "garbage", 1)} {
		if _, e := validateSDKDependency(dep, bad); e == nil {
			t.Fatal("invalid build record accepted")
		}
	}
	dep.Replace = &debug.Module{Path: "./fake"}
	if _, e := validateSDKDependency(dep, record); e == nil {
		t.Fatal("replace must stay rejected even with a record")
	}
}
