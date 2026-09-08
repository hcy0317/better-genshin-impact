package engine

import (
	_ "embed"
	"encoding/json"
)

//go:embed names.zh.json
var chineseNames json.RawMessage
