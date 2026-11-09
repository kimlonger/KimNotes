package main

import (
	"encoding/json"
	"net/url"
	"strings"
)

const guitarKeyColumns = `COALESCE(s.attributes->>'key',s.notation->>'key',''),COALESCE(s.attributes->'chords',s.notation->'chords','[]'::jsonb),COALESCE(s.attributes->'meter',s.notation->'meter','[]'::jsonb),COALESCE(s.attributes->'capo',s.notation->'capo')`
const guitarCardColumns = `COALESCE(s.artist,''),left(COALESCE(s.description,''),200),COALESCE(s.category,''),` + guitarStarsSQL + `,` + guitarKeyColumns + `,s.mime`

type guitarCard struct {
	artist, description, category, key string
	stars                              float64
	chords, meter, capo                json.RawMessage
	mime                               *string
}

func (c *guitarCard) scanFields() []any {
	return []any{&c.artist, &c.description, &c.category, &c.stars, &c.key, &c.chords, &c.meter, &c.capo, &c.mime}
}
func (c *guitarCard) apply(row map[string]any) {
	row["artist"] = c.artist
	row["description"] = c.description
	row["category"] = c.category
	row["difficultyStars"] = c.stars
	row["key"] = c.key
	row["chords"] = c.chords
	row["meter"] = c.meter
	row["capo"] = c.capo
	row["mime"] = c.mime
	if c.mime != nil && strings.HasPrefix(*c.mime, "image/") {
		row["thumbnailUrl"] = "/api/v1/apps/guitar/scores/" + url.PathEscape(row["id"].(string)) + "/file"
	}
}
