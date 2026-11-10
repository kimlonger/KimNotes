package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestContentSeedValidation(t *testing.T) {
	doc, err := decodeContentSeed(contentSeed)
	if err != nil || len(doc.Projects) != 3 || len(doc.Articles) != 16 || len(doc.Updates) != 11 {
		t.Fatal("embedded source-grounded content seed", err)
	}
	for _, change := range []struct {
		kind, field string
		value       any
	}{
		{"projects", "id", "../private"},
		{"projects", "url", "javascript:alert(1)"},
		{"projects", "url", "https://user:password@example.com"},
		{"projects", "privateUser", "not-public"},
		{"projects", "icon", "code icon"},
		{"projects", "name", strings.Repeat("x", 161)},
		{"projects", "features", strings.Split(strings.Repeat("x,", 64)+"x", ",")},
		{"articles", "project", "missing-project"},
		{"articles", "body", []string{"not a section pair"}},
		{"articles", "body", [][]string{{"Heading", strings.Repeat("x", 12001)}}},
		{"updates", "article", "missing-article"},
		{"updates", "date", "2026-02-30"},
	} {
		t.Run(change.kind+"/"+change.field, func(t *testing.T) {
			candidate, _ := decodeContentSeed(contentSeed)
			var list *[]json.RawMessage
			switch change.kind {
			case "projects":
				list = &candidate.Projects
			case "articles":
				list = &candidate.Articles
			default:
				list = &candidate.Updates
			}
			var obj map[string]any
			_ = json.Unmarshal((*list)[0], &obj)
			obj[change.field] = change.value
			(*list)[0], _ = json.Marshal(obj)
			raw, _ := json.Marshal(candidate)
			if _, err := decodeContentSeed(raw); err == nil {
				t.Fatal("invalid public content accepted")
			}
		})
	}
	missingURL, _ := decodeContentSeed(contentSeed)
	var project map[string]any
	_ = json.Unmarshal(missingURL.Projects[0], &project)
	delete(project, "url")
	missingURL.Projects[0], _ = json.Marshal(project)
	if err := validateContent(missingURL); err == nil {
		t.Fatal("missing nullable URL field was accepted")
	}
	badChecksum, _ := decodeContentSeed(contentSeed)
	_ = json.Unmarshal(badChecksum.Projects[1], &project)
	project["release"].(map[string]any)["sha256"] = strings.Repeat("z", 64)
	badChecksum.Projects[1], _ = json.Marshal(project)
	if err := validateContent(badChecksum); err == nil {
		t.Fatal("non-hex release checksum was accepted")
	}
	doc.Projects = append(doc.Projects, doc.Projects[0])
	raw, _ := json.Marshal(doc)
	if _, err := decodeContentSeed(raw); err == nil {
		t.Fatal("duplicate content IDs accepted")
	}
	if _, err := decodeContentSeed([]byte(strings.Repeat(" ", maxCatalogBytes+1))); err == nil {
		t.Fatal("oversized import accepted")
	}
}

func (e *testEnv) catalog(headers map[string]string) (int, catalogDocument, []byte, http.Header) {
	e.t.Helper()
	r, _ := http.NewRequest("GET", e.server.URL+"/api/v1/content/catalog", nil)
	for key, value := range headers {
		r.Header.Set(key, value)
	}
	response, err := http.DefaultClient.Do(r)
	if err != nil {
		e.t.Fatal(err)
	}
	defer response.Body.Close()
	raw, _ := io.ReadAll(response.Body)
	var doc catalogDocument
	_ = json.Unmarshal(raw, &doc)
	return response.StatusCode, doc, raw, response.Header
}

func TestContentImportAndPublishedCatalogue(t *testing.T) {
	e := newTestEnv(t)
	e.sql("DELETE FROM content_works")
	result, err := importContent(context.Background(), e.db, contentSeed, false)
	if err != nil || result != (contentImportResult{3, 16, 11}) {
		t.Fatal("first unified content import", result, err)
	}
	status, doc, raw, headers := e.catalog(nil)
	if status != 200 || len(doc.Projects) != 3 || len(doc.Articles) != 16 || len(doc.Updates) != 11 || len(raw) > maxCatalogBytes || len(doc.Version) != 64 {
		t.Fatal("anonymous published catalogue", status, len(doc.Projects), len(doc.Articles), len(doc.Updates))
	}
	etag := headers.Get("ETag")
	if etag != `"`+digest(string(raw))+`"` || headers.Get("Set-Cookie") != "" || headers.Get("Cache-Control") != "public, max-age=60, must-revalidate" {
		t.Fatal("public catalogue cache or cookie isolation", headers)
	}
	if status, _, body, h := e.catalog(map[string]string{"If-None-Match": etag}); status != 304 || len(body) != 0 || h.Get("ETag") != etag {
		t.Fatal("conditional public catalogue", status)
	}
	if status, _, _, _ := e.catalog(map[string]string{"If-None-Match": `"stale"`}); status != 200 {
		t.Fatal("stale content hash returned not-modified", status)
	}
	e.sql(`UPDATE content_works SET payload=jsonb_set(payload,'{name}','"Editorial name"'),status='draft' WHERE id='notes'`)
	if result, err = importContent(context.Background(), e.db, contentSeed, false); err != nil || result != (contentImportResult{}) {
		t.Fatal("default import overwrote existing edits", result, err)
	}
	var name, state string
	if err := e.db.QueryRow(context.Background(), "SELECT payload->>'name',status FROM content_works WHERE id='notes'").Scan(&name, &state); err != nil || name != "Editorial name" || state != "draft" {
		t.Fatal("existing publication state or edit replaced", name, state, err)
	}
	status, hidden, _, _ := e.catalog(nil)
	if status != 200 || len(hidden.Projects) != 2 || len(hidden.Articles) >= 16 || len(hidden.Updates) >= 11 || validateContent(hidden) != nil {
		t.Fatal("draft work or orphaned public references escaped", status)
	}
	for _, item := range hidden.Articles {
		obj, _ := contentObject(item, "articles")
		if obj["project"] == "notes" {
			t.Fatal("draft-associated article published")
		}
	}
	if result, err = importContent(context.Background(), e.db, contentSeed, true); err != nil || result != (contentImportResult{3, 16, 11}) {
		t.Fatal("explicit replacement import", result, err)
	}
	if status, _, _, h := e.catalog(nil); status != 200 || h.Get("ETag") != etag {
		t.Fatal("replacement did not restore original content", status)
	}
	// Published rows cannot leak unexpected account/editor-only fields even if
	// a privileged database edit bypasses the strict import validator.
	e.sql(`UPDATE content_works SET payload=payload||'{"privateEmail":"PRIVATE@example.com"}'::jsonb WHERE id='notes'`)
	if status, _, raw, _ := e.catalog(nil); status != 503 || strings.Contains(string(raw), "PRIVATE") {
		t.Fatal("private payload field escaped public catalogue", status)
	}
	if _, err := importContent(context.Background(), e.db, contentSeed, true); err != nil {
		t.Fatal(err)
	}
	e.app.config.Development, e.app.config.GatewaySecret = false, strings.Repeat("g", 32)
	if status, _, _, _ := e.catalog(nil); status != 403 {
		t.Fatal("catalogue bypassed central gateway", status)
	}
	if status, _, _, h := e.catalog(map[string]string{"X-Kim-Gateway": strings.Repeat("g", 32), "Authorization": "Bearer ignored", "Cookie": "__Host-kim_session_main=ignored"}); status != 200 || h.Get("Set-Cookie") != "" {
		t.Fatal("catalogue depended on personal credentials", status)
	}
}

func TestContentImportRollbackAndMigrationIdempotence(t *testing.T) {
	e := newTestEnv(t)
	e.sql(`CREATE FUNCTION reject_content_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic content import failure'; END $$`)
	e.sql("CREATE TRIGGER reject_content BEFORE INSERT ON content_updates FOR EACH ROW EXECUTE FUNCTION reject_content_update()")
	if _, err := importContent(context.Background(), e.db, contentSeed, false); err == nil {
		t.Fatal("synthetic failed import committed")
	}
	var articles, updates int
	if err := e.db.QueryRow(context.Background(), "SELECT (SELECT count(*) FROM content_articles),(SELECT count(*) FROM content_updates)").Scan(&articles, &updates); err != nil || articles != 0 || updates != 0 {
		t.Fatal("partial catalogue imported before failure", articles, updates, err)
	}
	e.sql("DROP TRIGGER reject_content ON content_updates")
	e.sql("DROP FUNCTION reject_content_update()")
	if _, err := importContent(context.Background(), e.db, contentSeed, false); err != nil {
		t.Fatal(err)
	}
	var namespace string
	if err := e.db.QueryRow(context.Background(), "SELECT current_schema()").Scan(&namespace); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		e.sql(strings.ReplaceAll(contentSchema, "kimcloud", namespace))
	}
	if status, doc, _, _ := e.catalog(nil); status != 200 || len(doc.Articles) != 16 {
		t.Fatal("reapplying migration lost editorial data", status)
	}
}

func TestPublishedWorksDriveAccountProjectValidation(t *testing.T) {
	e := newTestEnv(t)
	doc, _ := decodeContentSeed(contentSeed)
	var future map[string]any
	_ = json.Unmarshal(doc.Projects[0], &future)
	future["id"], future["name"], future["type"] = "future-tool", "Future tool", "开源工具"
	item, _ := json.Marshal(future)
	doc.Projects = append(doc.Projects, item)
	doc.Articles, doc.Updates = []json.RawMessage{}, []json.RawMessage{}
	seed, _ := json.Marshal(doc)
	if _, err := importContent(context.Background(), e.db, seed, false); err != nil {
		t.Fatal(err)
	}
	e.account("future-catalogue")
	one := e.mainLogin("future-catalogue")
	if status, out := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{"future-tool", "notes"}}, one); status != 200 {
		t.Fatal("new published work required hardcoded account changes", status, out)
	}
	if status, out := e.mainRequest("POST", "entries", map[string]string{"projectId": "future-tool", "action": "source"}, one); status != 201 {
		t.Fatal("new published work activity", status, out)
	}
	var app bool
	if err := e.db.QueryRow(context.Background(), "SELECT EXISTS(SELECT 1 FROM applications WHERE id='future-tool')").Scan(&app); err != nil || app {
		t.Fatal("editorial work was incorrectly registered as an auth app", app, err)
	}
	e.sql("UPDATE content_works SET status='draft' WHERE id='future-tool'")
	if status, _ := e.mainRequest("POST", "entries", map[string]string{"projectId": "future-tool", "action": "open"}, one); status != 400 {
		t.Fatal("unpublished work accepted for activity", status)
	}
	e.sql("UPDATE content_works SET status='published' WHERE id='future-tool'")
	e.app.config.MainProjectIDs = []string{"notes"}
	if status, _ := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{"future-tool"}}, one); status != 400 {
		t.Fatal("configured narrowing allowlist ignored", status)
	}
	if status, _ := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{"notes"}}, one); status != 200 {
		t.Fatal("allowed published work rejected", status)
	}
}

func TestCatalogueResponseBudget(t *testing.T) {
	e := newTestEnv(t)
	doc, _ := decodeContentSeed(contentSeed)
	var template map[string]any
	_ = json.Unmarshal(doc.Articles[0], &template)
	items := []map[string]any{}
	for i := 0; i < 9; i++ {
		obj := map[string]any{}
		for key, value := range template {
			obj[key] = value
		}
		obj["id"] = "large-article-" + string(rune('a'+i))
		body := [][]string{}
		for j := 0; j < 10; j++ {
			body = append(body, []string{"Section", strings.Repeat("a", 11800)})
		}
		obj["body"] = body
		items = append(items, obj)
	}
	data, _ := json.Marshal(items)
	e.sql(`INSERT INTO content_articles(id,project_id,payload) SELECT value->>'id',value->>'project',value FROM jsonb_array_elements($1::jsonb)`, data)
	if status, _, raw, _ := e.catalog(nil); status != 503 || len(raw) > maxCatalogBytes {
		t.Fatal("oversized catalogue returned partial or unbounded content", status, len(raw))
	}
}
