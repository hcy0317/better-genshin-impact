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

var expectedSDKVersion = "v1.15.2-0.20260905220630-1de5a4243879"

func verifySDK() (sdkIdentity, error) {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return sdkIdentity{}, errors.New("missing build dependency metadata")
	}
	for _, dependency := range info.Deps {
		if dependency.Path != "github.com/genshinsim/gcsim" {
			continue
		}
		if len(engine.Revision) != 40 || strings.ContainsAny(engine.Revision, "/\\ \t\n") || dependency.Replace != nil || dependency.Version != expectedSDKVersion || dependency.Sum == "" {
			return sdkIdentity{}, errors.New("gcsim dependency does not match the pinned, checksummed engine")
		}
		return sdkIdentity{Module: dependency.Path, Version: dependency.Version, Checksum: dependency.Sum, Revision: engine.Revision}, nil
	}
	return sdkIdentity{}, errors.New("gcsim dependency is absent from build metadata")
}
