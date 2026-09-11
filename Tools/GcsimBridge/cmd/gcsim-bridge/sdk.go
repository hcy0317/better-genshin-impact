package main

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"runtime/debug"
	"strings"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

type sdkIdentity struct {
	Module        string `json:"module"`
	Version       string `json:"version"`
	Checksum      string `json:"checksum"`
	Revision      string `json:"revision"`
	Source        string `json:"source,omitempty"`
	ModelRevision string `json:"modelRevision,omitempty"`
	OverlaySHA256 string `json:"overlaySHA256,omitempty"`
}

// Empty in ordinary builds. A source-verified isolated build replaces only the
// sdk_build.go file. This is build provenance, not a signature/trust certificate.
type vendorRecord struct {
	SchemaVersion int    `json:"schemaVersion"`
	Module        string `json:"module"`
	Version       string `json:"version"`
	Checksum      string `json:"checksum"`
	Revision      string `json:"revision"`
	ModelRevision string `json:"modelRevision"`
	OverlaySHA256 string `json:"overlaySHA256"`
}

func validateSDKDependency(dependency *debug.Module, rawRecord string) (sdkIdentity, error) {
	invalid := errors.New("gcsim dependency does not match the pinned, checksummed engine")
	if dependency.Path != "github.com/genshinsim/gcsim" || len(engine.Revision) != 40 || strings.ContainsAny(engine.Revision, "/\\ \t\n") || dependency.Replace != nil || dependency.Version != expectedSDKVersion {
		return sdkIdentity{}, invalid
	}
	id := sdkIdentity{Module: dependency.Path, Version: dependency.Version, Checksum: dependency.Sum, Revision: engine.Revision}
	if rawRecord == "" {
		if dependency.Sum == "" {
			return sdkIdentity{}, invalid
		}
		return id, nil
	}
	var r vendorRecord
	if len(rawRecord) > 4096 || json.Unmarshal([]byte(rawRecord), &r) != nil {
		return sdkIdentity{}, invalid
	}
	digest, e := hex.DecodeString(r.OverlaySHA256)
	if r.SchemaVersion != 1 || r.Module != dependency.Path || r.Version != expectedSDKVersion || r.Version != "v1.15.2-0.20260907234712-720f1a1f8167" || r.Revision != engine.Revision || r.Revision != "720f1a1f81673f9dc82f803e32239c4ad729bd0c" || r.Checksum != "h1:Rt/2gr0JK/o/a2Q/cv2AJarJd0XLma6W02mRkMI/n0k=" || r.ModelRevision != "sandrone-model-v1" || e != nil || len(digest) != 32 || dependency.Sum != "" {
		return sdkIdentity{}, invalid
	}
	id.Checksum = r.Checksum
	id.Source = "verified_vendor_overlay"
	id.ModelRevision = r.ModelRevision
	id.OverlaySHA256 = r.OverlaySHA256
	return id, nil
}

var expectedSDKVersion = "v1.15.2-0.20260907234712-720f1a1f8167"

func verifySDK() (sdkIdentity, error) {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return sdkIdentity{}, errors.New("missing build dependency metadata")
	}
	for _, dependency := range info.Deps {
		if dependency.Path != "github.com/genshinsim/gcsim" {
			continue
		}
		return validateSDKDependency(dependency, vendorBuildRecord)
	}
	return sdkIdentity{}, errors.New("gcsim dependency is absent from build metadata")
}
