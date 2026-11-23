package main

import (
	"context"
	"net/http"
	"reflect"
	"testing"
)

func TestNoteSourceDeviceLockedToFirstUpload(t *testing.T) {
	e := newTestEnv(t)
	const label = "device-origin"
	web := e.account(label)
	desktop := func(name, deviceID string) string {
		t.Helper()
		status, out, _ := e.sessionRequest(http.MethodPost, "kimnotes/auth/desktop/login", credentials{
			Email: label + "@example.com", Password: "Test-password-123", DeviceName: name, DeviceID: deviceID,
		}, nil, "", map[string]string{"X-Kim-Client": "desktop", "X-Kim-Auth-Version": "2"})
		if status != 200 {
			t.Fatalf("desktop login: %d %#v", status, out)
		}
		return out["token"].(string)
	}
	firstToken := desktop("办公室电脑", "test-office-code")
	secondToken := desktop("家里电脑", "test-home-code")
	want := map[string]any{"name": "办公室电脑", "kind": "desktop"}
	assertSource := func(note map[string]any, source map[string]any) {
		t.Helper()
		if !reflect.DeepEqual(note["sourceDevice"], source) {
			t.Fatalf("source mismatch: got %#v, want %#v", note["sourceDevice"], source)
		}
	}
	push := func(token string, op Operation) map[string]any {
		t.Helper()
		status, out, _ := e.sessionRequest(http.MethodPost, "kimnotes/sync/push", map[string]any{"operations": []Operation{op}}, nil, token, nil)
		if status != 200 {
			t.Fatalf("desktop push: %d %#v", status, out)
		}
		return out
	}
	note := func(out map[string]any) map[string]any {
		t.Helper()
		return result(out)["note"].(map[string]any)
	}

	id := newID()
	create := Operation{OperationID: newID(), NoteID: id, Action: "upsert", Document: &Document{Title: "test"}}
	first := push(firstToken, create)
	repeated := push(secondToken, create)
	assertSource(note(first), want)
	if !reflect.DeepEqual(first, repeated) {
		t.Fatal("an idempotent retry attributed the note to a different device or created another change")
	}
	assertSource(note(push(secondToken, Operation{
		OperationID: newID(), NoteID: id, BaseRevision: 1, Action: "upsert", Document: &Document{Title: "edited"},
	})), want)
	deleted := e.push(web, Operation{OperationID: newID(), NoteID: id, BaseRevision: 2, Action: "delete"})
	assertSource(note(deleted), want)
	status, page, _ := e.request(http.MethodGet, "kimnotes/sync/bootstrap?includeDeleted=true", nil, web)
	if status != 200 || len(page["notes"].([]any)) != 1 {
		t.Fatalf("bootstrap deleted notes: %d %#v", status, page)
	}
	assertSource(page["notes"].([]any)[0].(map[string]any), want)
	assertSource(note(push(secondToken, Operation{
		OperationID: newID(), NoteID: id, BaseRevision: 3, Action: "restore",
	})), want)
	for _, read := range []struct {
		path, field string
		count       int
	}{
		{"kimnotes/sync/bootstrap", "notes", 1},
		{"kimnotes/sync/pull?cursor=0", "changes", 4},
		{"kimnotes/notes/" + id + "/versions", "versions", 4},
	} {
		status, out, _ := e.request(http.MethodGet, read.path, nil, web)
		if status != 200 || len(out[read.field].([]any)) != read.count {
			t.Fatalf("%s: %d %#v", read.path, status, out)
		}
		for _, value := range out[read.field].([]any) {
			assertSource(value.(map[string]any), want)
		}
	}

	copyResult := push(secondToken, Operation{
		OperationID: newID(), NoteID: id, BaseRevision: 1, Action: "upsert", Document: &Document{Title: "stale edit"},
	})
	if result(copyResult)["status"] != "conflict_copy" || note(copyResult)["conflictOf"] != id {
		t.Fatalf("unexpected conflict response: %#v", copyResult)
	}
	assertSource(note(copyResult), map[string]any{"name": "家里电脑", "kind": "desktop"})

	webResult := e.push(web, Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Title: "created on web"}})
	assertSource(note(webResult), map[string]any{"name": "网页浏览器", "kind": "web"})
	status, out, _ := e.request(http.MethodPost, "kimnotes/sync/push", map[string]any{"operations": []map[string]any{{
		"operationId": newID(), "noteId": newID(), "action": "upsert", "document": map[string]any{"title": "forgery"},
		"sourceDevice": map[string]string{"name": "different computer", "kind": "desktop"},
	}}}, web)
	if status != 400 || out["code"] != "invalid_json" {
		t.Fatalf("client-supplied source was accepted: %d %#v", status, out)
	}
}

func TestLegacyNoteDoesNotAcquireSourceDeviceWhenEdited(t *testing.T) {
	e := newTestEnv(t)
	cookie := e.account("legacy-device-origin")
	id := newID()
	ctx := context.Background()
	tx, err := e.db.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback(ctx)
	var owner string
	if err = tx.QueryRow(ctx, "SELECT id::text FROM users WHERE email=$1", "legacy-device-origin@example.com").Scan(&owner); err != nil {
		t.Fatal(err)
	}
	if _, err = tx.Exec(ctx, "INSERT INTO notes(app_id,user_id,id,revision) VALUES('kimnotes',$1,$2,1)", owner, id); err != nil {
		t.Fatal(err)
	}
	if _, err = tx.Exec(ctx, `INSERT INTO note_versions(app_id,user_id,note_id,revision,snapshot,sequence) VALUES('kimnotes',$1,$2,1,
		jsonb_build_object('id',$2::uuid,'revision',1,'document','{"title":"legacy","attachments":[]}'::jsonb),1)`, owner, id); err != nil {
		t.Fatal(err)
	}
	if _, err = tx.Exec(ctx, "UPDATE user_apps SET cursor=1 WHERE app_id='kimnotes' AND user_id=$1", owner); err != nil {
		t.Fatal(err)
	}
	if err = tx.Commit(ctx); err != nil {
		t.Fatal(err)
	}

	out := e.push(cookie, Operation{OperationID: newID(), NoteID: id, BaseRevision: 1, Action: "upsert", Document: &Document{Title: "edited legacy"}})
	if _, hasSource := result(out)["note"].(map[string]any)["sourceDevice"]; hasSource {
		t.Fatalf("editing an older note incorrectly assigned its source: %#v", out)
	}
	status, page, _ := e.request(http.MethodGet, "kimnotes/sync/bootstrap", nil, cookie)
	if status != 200 || len(page["notes"].([]any)) != 1 {
		t.Fatalf("bootstrap existing note: %d %#v", status, page)
	}
	if _, hasSource := page["notes"].([]any)[0].(map[string]any)["sourceDevice"]; hasSource {
		t.Fatalf("older note was given a source in bootstrap: %#v", page)
	}
}
