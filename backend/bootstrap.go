package main

import (
	"encoding/json"
	"net/http"
	"strconv"

	"github.com/jackc/pgx/v5"
)

// bootstrap returns current live notes, not their old revisions. The first
// page pins an incremental cursor. Later pages may include newer documents;
// the client catches concurrent edits/deletes from that cursor after paging.
func (a *App) bootstrap(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	q := r.URL.Query()
	limit := 20
	var err error
	if q.Has("limit") {
		limit, err = strconv.Atoi(q.Get("limit"))
		if err != nil || limit < 1 || limit > 100 {
			return fail(400, "invalid_limit", "每页数量需要在 1–100 之间。")
		}
	}
	after := q.Get("after")
	if after != "" && (!validID(after) || !q.Has("cursor")) {
		return fail(400, "invalid_page", "初次同步分页无效。")
	}
	var requested int64
	if q.Has("cursor") {
		requested, err = parseCursor(q.Get("cursor"))
		if err != nil {
			return fail(400, "invalid_cursor", "同步检查点无效。")
		}
	}
	tx, err := a.db.BeginTx(r.Context(), pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if err != nil {
		return err
	}
	defer tx.Rollback(r.Context())
	var high int64
	if err = tx.QueryRow(r.Context(), "SELECT cursor FROM user_apps WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID).Scan(&high); err != nil {
		return err
	}
	if q.Has("cursor") {
		if requested > high {
			return fail(409, "cursor_ahead", "同步检查点超出当前账号范围。")
		}
		high = requested
	}
	var anchor any
	if after != "" {
		anchor = after
	}
	rows, err := tx.Query(r.Context(), `SELECT id::text,revision,document,conflict_of::text,created_at,updated_at
		FROM notes WHERE app_id=$1 AND user_id=$2 AND deleted_at IS NULL
		AND ($3::uuid IS NULL OR id>$3::uuid) ORDER BY id LIMIT $4`, p.AppID, p.UserID, anchor, limit+1)
	if err != nil {
		return err
	}
	defer rows.Close()
	items := []json.RawMessage{}
	total := 0
	more := false
	next := ""
	for rows.Next() {
		var note Note
		var document []byte
		if err = rows.Scan(&note.ID, &note.Revision, &document, &note.ConflictOf, &note.CreatedAt, &note.UpdatedAt); err != nil {
			return err
		}
		if err = json.Unmarshal(document, &note.Document); err != nil {
			return err
		}
		raw, e := json.Marshal(note)
		if e != nil {
			return e
		}
		if len(items) == limit || total+len(raw) > 4<<20 && len(items) > 0 {
			more = true
			break
		}
		if err = checkInlineReader(r, raw); err != nil {
			return err
		}
		items = append(items, raw)
		total += len(raw)
		next = note.ID
	}
	if err = rows.Err(); err != nil {
		return err
	}
	rows.Close()
	if err = tx.Commit(r.Context()); err != nil {
		return err
	}
	if !more {
		next = ""
	}
	writeJSON(w, 200, map[string]any{"notes": items, "cursor": strconv.FormatInt(high, 10), "nextAfter": next, "hasMore": more})
	return nil
}
