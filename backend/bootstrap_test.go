package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"testing"
)

func TestBootstrapLatestAndConcurrentCatchup(t *testing.T) {
	e := newTestEnv(t)
	one, other := e.account("bootstrap-owner"), e.account("bootstrap-other")
	ids := []string{"10000000-0000-4000-8000-000000000001", "20000000-0000-4000-8000-000000000002", "30000000-0000-4000-8000-000000000003", "40000000-0000-4000-8000-000000000004"}
	for revision := int64(0); revision < 20; revision++ {
		e.push(one, Operation{OperationID: newID(), NoteID: ids[0], BaseRevision: revision, Action: "upsert", Document: &Document{Title: fmt.Sprint(revision + 1), Text: "latest content"}})
	}
	for _, id := range ids[1:3] {
		e.push(one, Operation{OperationID: newID(), NoteID: id, Action: "upsert", Document: &Document{Title: "second note"}})
	}
	e.push(one, Operation{OperationID: newID(), NoteID: ids[2], BaseRevision: 1, Action: "delete"})
	status, first, _ := e.request("GET", "kimnotes/sync/bootstrap?limit=1", nil, one)
	if status != 200 || first["cursor"] != "23" || first["hasMore"] != true || first["nextAfter"] != ids[0] {
		t.Fatal("initial checkpoint/page", status, first)
	}
	notes := first["notes"].([]any)
	if len(notes) != 1 || notes[0].(map[string]any)["revision"] != float64(20) {
		t.Fatal("initial download replayed old revisions", first)
	}
	// Edits and deletion while a new device is paging must remain in the
	// incremental tail, even if a later page already contains a newer note.
	e.push(one, Operation{OperationID: newID(), NoteID: ids[0], BaseRevision: 20, Action: "upsert", Document: &Document{Title: "21"}})
	e.push(one, Operation{OperationID: newID(), NoteID: ids[1], BaseRevision: 1, Action: "delete"})
	e.push(one, Operation{OperationID: newID(), NoteID: ids[3], Action: "upsert", Document: &Document{Title: "created while downloading"}})
	status, second, _ := e.request("GET", "kimnotes/sync/bootstrap?limit=1&cursor=23&after="+ids[0], nil, one)
	if status != 200 || second["cursor"] != "23" || second["hasMore"] != false || second["nextAfter"] != "" {
		t.Fatal("checkpoint moved while paging", status, second)
	}
	notes = second["notes"].([]any)
	if len(notes) != 1 || notes[0].(map[string]any)["id"] != ids[3] {
		t.Fatal("latest live notes or tombstone filtering", second)
	}
	status, tail, _ := e.request("GET", "kimnotes/sync/pull?cursor=23", nil, one)
	if status != 200 || len(tail["changes"].([]any)) != 3 || tail["cursor"] != "26" {
		t.Fatal("concurrent changes lost after initial download", status, tail)
	}
	if s, out, _ := e.request("GET", "kimnotes/sync/bootstrap", nil, other); s != 200 || len(out["notes"].([]any)) != 0 {
		t.Fatal("cross-user initial download", s, out)
	}
	if _, err := e.db.Exec(context.Background(), "INSERT INTO applications(id,name) VALUES('another-app','Another')"); err != nil {
		t.Fatal(err)
	}
	s, _, response := e.request("POST", "another-app/auth/login", credentials{Email: "bootstrap-owner@example.com", Password: "Test-password-123"}, nil)
	if s != 200 {
		t.Fatal("other app login", s)
	}
	var appCookie *http.Cookie
	for _, cookie := range response.Cookies() {
		if cookie.Name == e.app.cookieName("another-app") {
			appCookie = cookie
		}
	}
	if appCookie == nil {
		t.Fatal("other app session cookie missing")
	}
	if s, out, _ := e.request("GET", "another-app/sync/bootstrap", nil, appCookie); s != 200 || len(out["notes"].([]any)) != 0 {
		t.Fatal("cross-application initial download", s, out)
	}
	for _, query := range []string{"?limit=0", "?limit=101", "?cursor=-1", "?after=bad&cursor=0", "?after=" + ids[0]} {
		if s, _, _ := e.request("GET", "kimnotes/sync/bootstrap"+query, nil, one); s != 400 {
			t.Fatal("invalid initial page accepted", query, s)
		}
	}
	if s, _, _ := e.request("GET", "kimnotes/sync/bootstrap?cursor=99999", nil, one); s != 409 {
		t.Fatal("future checkpoint accepted", s)
	}
	if s, _, _ := e.request("GET", "kimnotes/sync/bootstrap", nil, nil); s != 401 {
		t.Fatal("anonymous initial download", s)
	}
}

func TestBootstrapPageBudget(t *testing.T) {
	e := newTestEnv(t)
	one := e.account("bootstrap-budget")
	for n := 0; n < 17; n++ {
		e.push(one, Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Text: strings.Repeat("x", 256<<10)}})
	}
	status, first, _ := e.request("GET", "kimnotes/sync/bootstrap?limit=100", nil, one)
	if status != 200 || first["hasMore"] != true || first["cursor"] != "17" {
		t.Fatal("large documents must page within the response budget", status)
	}
	items := first["notes"].([]any)
	if len(items) == 0 || len(items) >= 17 {
		t.Fatal("large first page size", len(items))
	}
	var size int
	for _, item := range items {
		raw, _ := json.Marshal(item)
		size += len(raw)
	}
	if size > 4<<20 {
		t.Fatal("initial download exceeded document byte budget", size)
	}
	status, second, _ := e.request("GET", "kimnotes/sync/bootstrap?limit=100&cursor=17&after="+first["nextAfter"].(string), nil, one)
	if status != 200 || second["hasMore"] != false || len(items)+len(second["notes"].([]any)) != 17 {
		t.Fatal("response byte pagination skipped documents", status)
	}
}
