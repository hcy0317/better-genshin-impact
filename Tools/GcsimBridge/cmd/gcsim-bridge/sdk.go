package main

import (
	"errors"
	"runtime/debug"
	"strings"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

type sdkIdentity struct {
	Module   string `json:"module"`
	Version  string `json:"version"`
	Checksum string `json:"checksum"`
	Revision string `json:"revision"`
}

func verifySDK() (sdkIdentity, error) {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return sdkIdentity{}, errors.New("missing build dependency metadata")
	}
	for _, dependency := range info.Deps {
		if dependency.Path != "github.com/genshinsim/gcsim" {
			continue
		}
		if dependency.Replace != nil || !strings.HasSuffix(dependency.Version, "-"+engine.Revision[:12]) || dependency.Sum == "" {
			return sdkIdentity{}, errors.New("gcsim dependency does not match the pinned, checksummed engine")
		}
		return sdkIdentity{Module: dependency.Path, Version: dependency.Version, Checksum: dependency.Sum, Revision: engine.Revision}, nil
	}
	return sdkIdentity{}, errors.New("gcsim dependency is absent from build metadata")
}
