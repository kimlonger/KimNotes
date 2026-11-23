package main

import (
	"encoding/base64"
	"encoding/json"
	"net/http"
	"time"
)

type guitarHistoryCursor struct {
	Day    string     `json:"day"`
	At     *time.Time `json:"at"`
	Source string     `json:"source"`
	ID     string     `json:"id"`
}

func parseGuitarHistoryCursor(value string) (guitarHistoryCursor, error) {
	var cursor guitarHistoryCursor
	if value == "" {
		return cursor, nil
	}
	if len(value) > 350 {
		return cursor, fail(400, "invalid_cursor", "练习记录页码无效。")
	}
	raw, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil || json.Unmarshal(raw, &cursor) != nil || !validID(cursor.ID) {
		return cursor, fail(400, "invalid_cursor", "练习记录页码无效。")
	}
	if _, err = time.Parse(time.DateOnly, cursor.Day); err != nil {
		return cursor, fail(400, "invalid_cursor", "练习记录页码无效。")
	}
	switch cursor.Source {
	case "manual", "estimate":
		if cursor.At == nil {
			return cursor, nil
		}
	case "score", "task":
		if cursor.At != nil {
			return cursor, nil
		}
	}
	return cursor, fail(400, "invalid_cursor", "练习记录页码无效。")
}

func (a *App) guitarPracticeHistory(w http.ResponseWriter, r *http.Request) error {
	size, err := guitarCatalogNumber(r, "pageSize", 12, 60)
	if err != nil {
		return err
	}
	cursor, err := parseGuitarHistoryCursor(r.URL.Query().Get("cursor"))
	if err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT e.day::text,e.at,e.source,e.id::text,to_jsonb(e)-'user_id'-'task_id'
 FROM (`+guitarPracticeEvents+`) e WHERE e.user_id=$1 AND (NULLIF($2,'') IS NULL OR
 (e.day,COALESCE(e.at,'infinity'::timestamptz),e.source,e.id)
 < (NULLIF($2,'')::date,COALESCE($3::timestamptz,'infinity'::timestamptz),$4,NULLIF($5,'')::uuid))
 ORDER BY e.day DESC,COALESCE(e.at,'infinity'::timestamptz) DESC,e.source DESC,e.id DESC LIMIT $6`,
		identity(r).UserID, cursor.Day, cursor.At, cursor.Source, cursor.ID, size+1)
	if err != nil {
		return err
	}
	defer rows.Close()
	records := make([]json.RawMessage, 0, size)
	next := ""
	var last guitarHistoryCursor
	for rows.Next() {
		var position guitarHistoryCursor
		var record json.RawMessage
		if err := rows.Scan(&position.Day, &position.At, &position.Source, &position.ID, &record); err != nil {
			return err
		}
		if len(records) == size {
			encoded, err := json.Marshal(last)
			if err != nil {
				return err
			}
			next = base64.RawURLEncoding.EncodeToString(encoded)
			break
		}
		records = append(records, record)
		last = position
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"records": records, "nextCursor": next})
	return nil
}
