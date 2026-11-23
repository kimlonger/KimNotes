package main

import (
	"context"
	"encoding/json"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestNoteCanonicalMigrationRejectsAmbiguousHeads(t *testing.T) {
	for _, condition := range []string{"missing version", "different document", "newer version", "invalid document"} {
		t.Run(condition, func(t *testing.T) {
			e := newTestEnvWithNoteSchema(t, false)
			ctx := context.Background()
			e.account("canonical-guard")
			var user string
			if err := e.db.QueryRow(ctx, "SELECT id::text FROM users WHERE email='canonical-guard@example.com'").Scan(&user); err != nil {
				t.Fatal(err)
			}
			e.sql(migrationFixture("027_note_sync_compaction"))
			id := newID()
			e.sql(`INSERT INTO notes(app_id,user_id,id,revision,document) VALUES('kimnotes',$1,$2,1,'{"text":"original"}')`, user, id)
			if condition != "missing version" {
				document := `{"text":"original"}`
				if condition == "different document" {
					document = `{"text":"other"}`
				} else if condition == "invalid document" {
					document = `null`
				}
				e.sql(`INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot,sequence) VALUES('kimnotes',$1,$2,1,jsonb_build_object('document',$3::jsonb),1)`, user, id, document)
				if condition == "newer version" {
					e.sql(`INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot,sequence) VALUES('kimnotes',$1,$2,2,'{"document":{"text":"newer"}}',2)`, user, id)
				}
			}
			tx, err := e.db.Begin(ctx)
			if err != nil {
				t.Fatal(err)
			}
			_, err = tx.Exec(ctx, migrationFixture("032_note_canonical_document"))
			_ = tx.Rollback(ctx)
			if err == nil || !strings.Contains(err.Error(), "every head to match its latest version document") {
				t.Fatalf("unsafe head accepted: %v", err)
			}
			var text string
			if err = e.db.QueryRow(ctx, "SELECT document->>'text' FROM notes WHERE id=$1", id).Scan(&text); err != nil || text != "original" {
				t.Fatalf("failed migration changed original document: %q %v", text, err)
			}
		})
	}
}

func TestNoteCanonicalMetadataHistoryAndHeadConstraint(t *testing.T) {
	e := newTestEnvWithNoteSchema(t, false)
	ctx := context.Background()
	cookie := e.account("canonical-preserve")
	var user string
	if err := e.db.QueryRow(ctx, "SELECT id::text FROM users WHERE email='canonical-preserve@example.com'").Scan(&user); err != nil {
		t.Fatal(err)
	}
	e.sql(migrationFixture("027_note_sync_compaction"))
	stamp := time.Date(2026, 1, 2, 3, 4, 5, 123000000, time.UTC)
	id, conflict := newID(), newID()
	doc := Document{Text: "canonical text", RTFGzip: packedRTF(`{\rtf1 canonical text}`), Attachments: []string{}, ThemeID: 3}
	note := Note{ID: id, Revision: 1, Document: doc, CreatedAt: stamp, UpdatedAt: stamp}
	snapshot, _ := json.Marshal(note)
	body, _ := json.Marshal(doc)
	// Preserve typed head metadata even for a legacy snapshot with omitted or
	// different metadata. Only the equal document is being canonicalized.
	e.sql(`INSERT INTO notes(app_id,user_id,id,revision,document,conflict_of,deleted_at,created_at,updated_at) VALUES('kimnotes',$1,$2,1,$3,$4,$5,$5,$5)`, user, id, body, conflict, stamp)
	e.sql(`INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot,sequence) VALUES('kimnotes',$1,$2,1,$3,1)`, user, id, snapshot)
	e.sql(`UPDATE user_apps SET cursor=1 WHERE app_id='kimnotes' AND user_id=$1`, user)
	e.sql(migrationFixture("032_note_canonical_document"))
	var absent, deferred bool
	if err := e.db.QueryRow(ctx, `SELECT NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='notes' AND column_name='document'),
		(SELECT condeferrable AND condeferred AND convalidated FROM pg_constraint WHERE conrelid='notes'::regclass AND conname='notes_current_version')`).Scan(&absent, &deferred); err != nil || !absent || !deferred {
		t.Fatalf("canonical storage not enforced: absent=%v deferred=%v err=%v", absent, deferred, err)
	}
	status, out, _ := e.request("GET", "kimnotes/sync/bootstrap?includeDeleted=true", nil, cookie)
	if status != 200 || len(out["notes"].([]any)) != 1 {
		t.Fatalf("canonical bootstrap: %d %#v", status, out)
	}
	got := out["notes"].([]any)[0].(map[string]any)
	if got["deletedAt"] == nil || got["conflictOf"] != conflict || got["document"].(map[string]any)["rtfGzip"] != doc.RTFGzip {
		t.Fatalf("head metadata or rich text changed: %#v", got)
	}
	tx, err := e.db.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = tx.Exec(ctx, `UPDATE notes SET revision=999 WHERE app_id='kimnotes' AND user_id=$1 AND id=$2`, user, id); err != nil {
		t.Fatal("head FK should permit transient pointers until commit", err)
	}
	if err = tx.Commit(ctx); err == nil {
		t.Fatal("dangling current revision committed")
	}
	status, out, _ = e.request("GET", "kimnotes/notes/"+id+"/versions", nil, cookie)
	if status != 200 || len(out["versions"].([]any)) != 1 {
		t.Fatalf("history endpoint changed: %d %#v", status, out)
	}
	var original map[string]any
	_ = json.Unmarshal(snapshot, &original)
	if !reflect.DeepEqual(out["versions"].([]any)[0], original) {
		t.Fatalf("immutable snapshot rewritten: %#v", out)
	}
}
