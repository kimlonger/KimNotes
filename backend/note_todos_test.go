package main

import (
	"context"
	"net/http"
	"net/url"
	"testing"
	"time"
)

func TestNoteTodoMirror(t *testing.T) {
	e := newTestEnv(t)
	e.sql(migrationFixture("039_note_todos"))
	a := e.account("todo-owner")
	b := e.account("todo-other")
	token := e.desktop("todo-owner", true)["token"].(string)
	const path = "kimnotes/todos"
	now := time.Now().UTC().Truncate(time.Second)
	due := now.Add(time.Hour)
	id := newID()
	first := noteTodoChange{noteTodo: noteTodo{ID: id, Text: "约吉他课", CreatedAt: &now, DueAt: &due, Repeat: 2}, Revision: 1}
	push := func(expectedStatus int, changes ...noteTodoChange) {
		t.Helper()
		status, data, _ := e.sessionRequest(http.MethodPost, path, noteTodoPush{changes}, nil, token, nil)
		if status != expectedStatus {
			t.Fatalf("push status %d, want %d: %v", status, expectedStatus, data)
		}
	}
	list := func(cookie *http.Cookie) []any {
		t.Helper()
		status, data, res := e.request(http.MethodGet, path, nil, cookie)
		if status != 200 || res.Header.Get("Cache-Control") != "private, no-store" {
			t.Fatalf("list returned %d (%v), cache %q", status, data, res.Header.Get("Cache-Control"))
		}
		return data["todos"].([]any)
	}
	timeEquals := func(raw any, want time.Time) bool {
		value, _ := raw.(string)
		at, err := time.Parse(time.RFC3339, value)
		return err == nil && at.Equal(want)
	}
	if status, _, _ := e.request(http.MethodGet, path, nil, nil); status != 401 {
		t.Fatalf("unauthenticated read returned %d", status)
	}
	if status, _, _ := e.request(http.MethodPost, path, noteTodoPush{[]noteTodoChange{first}}, a); status != 403 {
		t.Fatalf("web write returned %d", status)
	}
	invalid := first
	invalid.ID, invalid.Text = newID(), " \t"
	push(400, first, invalid)
	if len(list(a)) != 0 {
		t.Fatal("invalid batch left a partial write")
	}

	push(200, first)
	items := list(a)
	if len(items) != 1 || !timeEquals(items[0].(map[string]any)["dueAt"], due) || items[0].(map[string]any)["repeat"] != float64(2) {
		t.Fatalf("scheduled task missing: %v", items)
	}
	if len(list(b)) != 0 {
		t.Fatal("another user's session can read the task")
	}
	if status, _, _ := e.sessionRequest(http.MethodPost, path, noteTodoPush{[]noteTodoChange{first}}, nil, token, map[string]string{"X-Kim-Expected-User": newID()}); status != 409 {
		t.Fatalf("changed account write returned %d", status)
	}
	e.sql("INSERT INTO applications(id, name) VALUES ('todo-fixture', 'other app')")
	status, _, res := e.request(http.MethodPost, "todo-fixture/auth/login", credentials{Email: "todo-owner@example.com", Password: "Test-password-123"}, nil)
	if status != 200 {
		t.Fatalf("other app login returned %d", status)
	}
	if status, _, _ := e.request(http.MethodGet, "todo-fixture/todos", nil, res.Cookies()[0]); status != 404 {
		t.Fatalf("other app read returned %d", status)
	}

	completed := first
	completed.Revision, completed.Text = 2, "已约好吉他课"
	completed.Done, completed.DoneAt = true, &now
	push(200, completed)
	push(200, first, completed)
	item := list(a)[0].(map[string]any)
	if item["text"] != completed.Text || item["done"] != true || !timeEquals(item["doneAt"], now) {
		t.Fatalf("retry or stale write replaced the completed task: %v", item)
	}
	deleted := noteTodoChange{noteTodo: noteTodo{ID: id}, Revision: 3, DeletedAt: &now}
	push(200, deleted)
	completed.Revision = 4
	push(200, completed)
	if len(list(a)) != 0 {
		t.Fatal("deleted task is still visible or was resurrected by a later write")
	}
	var body string
	if err := e.db.QueryRow(context.Background(), `SELECT body FROM note_todos WHERE id=$1 AND deleted_at IS NOT NULL`, id).Scan(&body); err != nil || body != completed.Text {
		t.Fatalf("logical deletion failed to retain task: %q, %v", body, err)
	}
}

func TestNoteTodoPagination(t *testing.T) {
	e := newTestEnv(t)
	e.sql(migrationFixture("039_note_todos"))
	cookie := e.account("todo-pages")
	e.sql(`INSERT INTO note_todos (app_id,user_id,id,revision,body,created_at,done,repeat_rule)
	  SELECT app_id,user_id,gen_random_uuid(),1,'task',now(),false,0
	  FROM user_apps CROSS JOIN generate_series(1, 101) WHERE app_id='kimnotes'`)
	status, first, _ := e.request(http.MethodGet, "kimnotes/todos", nil, cookie)
	if status != 200 || len(first["todos"].([]any)) != noteTodoPageSize || first["nextCursor"] == nil {
		t.Fatalf("first page status %d or incorrect cursor", status)
	}
	status, next, _ := e.request(http.MethodGet, "kimnotes/todos?cursor="+url.QueryEscape(first["nextCursor"].(string)), nil, cookie)
	if status != 200 || len(next["todos"].([]any)) != 1 || next["nextCursor"] != nil {
		t.Fatalf("last page status %d or incorrect cursor: %v", status, next)
	}
	firstIDs := map[string]bool{}
	for _, item := range first["todos"].([]any) {
		firstIDs[item.(map[string]any)["id"].(string)] = true
	}
	if firstIDs[next["todos"].([]any)[0].(map[string]any)["id"].(string)] {
		t.Fatal("equal creation times repeated an item across pages")
	}
}
