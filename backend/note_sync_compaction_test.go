package main

import (
	"context"
	_ "embed"
	"encoding/json"
	"reflect"
	"strings"
	"testing"
	"time"
)

//go:embed maintenance/rollback_027_note_sync_compaction.sql
var noteCompactionRollback string

//go:embed maintenance/rollback_032_note_canonical_document.sql
var noteCanonicalRollback string

func TestNoteCompactionPreservesLegacyReplayAndRollback(t *testing.T) {
	e := newTestEnvWithNoteSchema(t, false)
	ctx := context.Background()
	cookie := e.account("compaction-legacy")
	var user, schemaName string
	if err := e.db.QueryRow(ctx, `SELECT id::text,current_schema() FROM users WHERE email='compaction-legacy@example.com'`).Scan(&user, &schemaName); err != nil {
		t.Fatal(err)
	}
	exec := func(sql string, args ...any) {
		t.Helper()
		if _, err := e.db.Exec(ctx, sql, args...); err != nil {
			t.Fatal(err)
		}
	}
	noteID := newID()
	stamp := time.Date(2026, 1, 2, 3, 4, 5, 123000000, time.UTC)
	doc := Document{Title: "Original", Text: "Saved before migration", Attachments: []string{}}
	oldNote := Note{ID: noteID, Revision: 1, Document: doc, CreatedAt: stamp, UpdatedAt: stamp}
	oldSnapshot, _ := json.Marshal(oldNote)
	oldDocument, _ := json.Marshal(doc)
	exec(`INSERT INTO notes(app_id,user_id,id,revision,document,created_at,updated_at)
		VALUES('kimnotes',$1,$2,1,$3,$4,$4)`, user, noteID, oldDocument, stamp)
	exec(`INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot) VALUES('kimnotes',$1,$2,1,$3)`, user, noteID, oldSnapshot)
	exec(`INSERT INTO note_changes(app_id,user_id,note_id,revision,sequence) VALUES('kimnotes',$1,$2,1,1)`, user, noteID)
	exec(`UPDATE user_apps SET cursor=1 WHERE app_id='kimnotes' AND user_id=$1`, user)

	oldUpsert := Operation{OperationID: newID(), NoteID: noteID, Action: "upsert", Document: &doc}
	oldConflict := Operation{OperationID: newID(), NoteID: noteID, Action: "delete"}
	otherNote := oldNote
	otherNote.Document.Text = "Legacy reply does not exactly match its snapshot"
	for _, old := range []struct {
		op   Operation
		note Note
		code string
	}{{oldUpsert, oldNote, "applied"}, {oldConflict, otherNote, "conflict"}} {
		request, _ := json.Marshal(old.op)
		saved, _ := json.Marshal(OperationResult{OperationID: old.op.OperationID, Status: old.code, Note: old.note})
		exec(`INSERT INTO operations(app_id,user_id,id,request_hash,response) VALUES('kimnotes',$1,$2,$3,$4)`,
			user, old.op.OperationID, digest(string(request)), saved)
	}

	exec(`CREATE TABLE schema_migrations(version text PRIMARY KEY)`)
	migrateNotes := func() {
		exec(migrationFixture("027_note_sync_compaction"))
		exec(`INSERT INTO schema_migrations(version) VALUES('027_note_sync_compaction')`)
		exec(migrationFixture("032_note_canonical_document"))
		exec(`INSERT INTO schema_migrations(version) VALUES('032_note_canonical_document')`)
	}
	migrateNotes()
	var compacted, fallback int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FILTER (WHERE result_note_id IS NOT NULL AND NOT (response ? 'note')),
		count(*) FILTER (WHERE result_note_id IS NULL AND response ? 'note') FROM operations`).Scan(&compacted, &fallback); err != nil || compacted != 1 || fallback != 1 {
		t.Fatalf("legacy receipts compacted=%d fallback=%d: %v", compacted, fallback, err)
	}
	first := e.push(cookie, oldUpsert)
	conflict := e.push(cookie, oldConflict)
	if first["cursor"] != "1" || conflict["cursor"] != "1" || conflict["results"].([]any)[0].(map[string]any)["status"] != "conflict" {
		t.Fatalf("legacy replay changed cursor or conflict status: %#v %#v", first, conflict)
	}
	if reply := conflict["results"].([]any)[0].(map[string]any)["note"].(map[string]any); reply["document"].(map[string]any)["text"] != otherNote.Document.Text {
		t.Fatalf("legacy fallback response changed: %#v", reply)
	}
	update := Operation{OperationID: newID(), NoteID: noteID, BaseRevision: 1, Action: "upsert", Document: &Document{Text: "Changed after migration"}}
	updated := e.push(cookie, update)
	noop := Operation{OperationID: newID(), NoteID: noteID, BaseRevision: 2, Action: "restore"}
	unchanged := e.push(cookie, noop)
	if updated["cursor"] != "2" || !reflect.DeepEqual(unchanged, e.push(cookie, noop)) || unchanged["cursor"] != "2" {
		t.Fatal("new update or no-op unexpectedly changed the sync stream")
	}
	if result := e.push(cookie, oldUpsert); !reflect.DeepEqual(result["results"], first["results"]) {
		t.Fatalf("old retry used mutable current note: %#v", result)
	}
	for _, page := range []struct{ start, next string }{{"0", "1"}, {"1", "2"}} {
		status, out, _ := e.request("GET", "kimnotes/sync/pull?limit=1&cursor="+page.start, nil, cookie)
		if status != 200 || out["cursor"] != page.next || out["hasMore"] != (page.next != "2") {
			t.Fatalf("unexpected pull page from %s: %d %#v", page.start, status, out)
		}
	}

	exec(strings.ReplaceAll(noteCanonicalRollback, "kimcloud", schemaName))
	exec(strings.ReplaceAll(noteCompactionRollback, "kimcloud", schemaName))
	var changed, responses int
	if err := e.db.QueryRow(ctx, `SELECT (SELECT count(*) FROM note_changes WHERE sequence IN (1,2)),
		(SELECT count(*) FROM operations WHERE response ? 'note')`).Scan(&changed, &responses); err != nil || changed != 2 || responses != 4 {
		t.Fatalf("rollback omitted current rows: changes=%d responses=%d: %v", changed, responses, err)
	}
	migrateNotes()
	if result := e.push(cookie, update); !reflect.DeepEqual(result, updated) {
		t.Fatalf("result after rollback and migration changed: %#v", result)
	}
}
