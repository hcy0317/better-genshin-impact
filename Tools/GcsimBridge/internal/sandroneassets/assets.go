// Package sandroneassets holds SDK-internal source files as build resources;
// importing it does not register a character or bypass SDK validation.
package sandroneassets

import (
	"embed"
	"strings"
)

//go:embed *.go.txt
var files embed.FS

func Sources() map[string][]byte {
	entries,err:=files.ReadDir("."); if err!=nil {panic(err)}
	out:=map[string][]byte{}
	for _,entry:=range entries {
		b,err:=files.ReadFile(entry.Name());if err!=nil {panic(err)}
		out[strings.TrimSuffix(entry.Name(),".txt")]=b
	}
	return out
}
