package sdkoverlay_test

import (
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/sdkoverlay"
	"os"
	"path/filepath"
	"testing"
)

func TestVendorProofRequiresMatchingSourceBytes(t *testing.T) {
	source, vendor := t.TempDir(), t.TempDir()
	for _, root := range []string{source, vendor} {
		if err := os.WriteFile(filepath.Join(root, "file.go"), []byte("package fixture\n"), 0600); err != nil {
			t.Fatal(err)
		}
	}
	digest, err := sdkoverlay.VerifyVendorSource(vendor, source)
	if err != nil || len(digest) != 64 {
		t.Fatalf("matching copy: %q %v", digest, err)
	}
	if err := os.WriteFile(filepath.Join(vendor, "file.go"), []byte("package altered\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := sdkoverlay.VerifyVendorSource(vendor, source); err == nil {
		t.Fatal("tampered vendor accepted")
	}
}
