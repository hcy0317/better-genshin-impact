package sdkoverlay

import (
	"bytes"
	"crypto/sha256"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
)

// VerifyVendorSource checks an isolated go-mod-vendor copy against the module
// cache after go mod verify. This is a build provenance check, not a signature.
func VerifyVendorSource(vendor, source string) (string, error) {
	for _, root := range []string{vendor, source} {
		st, err := os.Lstat(root)
		if err != nil {
			return "", err
		}
		if !st.IsDir() || st.Mode()&os.ModeSymlink != 0 {
			return "", fmt.Errorf("source root must be an ordinary directory: %s", root)
		}
	}
	digest := sha256.New()
	count := 0
	err := filepath.WalkDir(vendor, func(file string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.Type()&os.ModeSymlink != 0 {
			return fmt.Errorf("linked vendor entry: %s", file)
		}
		if entry.IsDir() {
			return nil
		}
		if !entry.Type().IsRegular() {
			return fmt.Errorf("nonregular vendor entry: %s", file)
		}
		rel, err := filepath.Rel(vendor, file)
		if err != nil {
			return err
		}
		original := filepath.Join(source, rel)
		st, err := os.Lstat(original)
		if err != nil {
			return err
		}
		if !st.Mode().IsRegular() {
			return fmt.Errorf("nonregular module source: %s", original)
		}
		a, err := os.ReadFile(file)
		if err != nil {
			return err
		}
		b, err := os.ReadFile(original)
		if err != nil {
			return err
		}
		if !bytes.Equal(a, b) {
			return fmt.Errorf("vendor differs from verified SDK source: %s", rel)
		}
		fmt.Fprintf(digest, "%s\x00%x\n", filepath.ToSlash(rel), sha256.Sum256(a))
		count++
		return nil
	})
	if err != nil {
		return "", err
	}
	if count == 0 {
		return "", fmt.Errorf("empty vendor source")
	}
	return fmt.Sprintf("%x", digest.Sum(nil)), nil
}
