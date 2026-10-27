package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"github.com/jackc/pgx/v5/pgxpool"
	"image"
	"image/png"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestPassword(t *testing.T) {
	h := hashPassword("测试-password-123")
	if !checkPassword("测试-password-123", h) || checkPassword("wrong-password", h) {
		t.Fatal("password verification")
	}
	if h == hashPassword("测试-password-123") {
		t.Fatal("salt reused")
	}
	for _, s := range []string{"", "bad", strings.Replace(h, "m=19456", "m=999999999", 1)} {
		if checkPassword("x", s) {
			t.Fatal("invalid hash accepted")
		}
	}
}
func TestValidation(t *testing.T) {
	if validPassword(strings.Repeat("密", 50)) != nil || validPassword(strings.Repeat("密", 129)) == nil {
		t.Fatal("Unicode password length")
	}
	if !validID(newID()) || validID("../../secret") {
		t.Fatal("UUID validation")
	}
	d := Document{Title: "<script>literal</script>", ThemeID: 2}
	if e := d.validate(); e != nil {
		t.Fatal(e)
	}
	d.Attachments = []string{"https://outside"}
	if d.validate() == nil {
		t.Fatal("arbitrary URL accepted")
	}
	if _, e := normalizeEmail("a\r\nBcc:x@example.com"); e == nil {
		t.Fatal("email injection")
	}
}

type testEnv struct {
	app    *App
	server *httptest.Server
	db     *pgxpool.Pool
	t      *testing.T
}

func newTestEnv(t *testing.T) *testEnv {
	t.Helper()
	dsn := os.Getenv("KIMCLOUD_TEST_DATABASE_URL")
	if dsn == "" {
		dsn = os.Getenv("DATABASE_URL")
	}
	if dsn == "" {
		t.Skip("DATABASE_URL or KIMCLOUD_TEST_DATABASE_URL required for database integration")
	}
	ctx := context.Background()
	admin, e := pgxpool.New(ctx, dsn)
	if e != nil {
		t.Fatal("database configuration invalid")
	}
	name := "kimcloud_test_" + strings.ReplaceAll(newID(), "-", "")
	if _, e = admin.Exec(ctx, "CREATE SCHEMA "+name); e != nil {
		admin.Close()
		t.Fatal("test schema create:", e)
	}
	t.Cleanup(func() {
		_, e := admin.Exec(ctx, "DROP SCHEMA "+name+" CASCADE")
		admin.Close()
		if e != nil {
			t.Error("test schema cleanup:", e)
		}
	})
	pc, e := pgxpool.ParseConfig(dsn)
	if e != nil {
		t.Fatal(e)
	}
	pc.ConnConfig.RuntimeParams["search_path"] = name + ",public"
	db, e := pgxpool.NewWithConfig(ctx, pc)
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(db.Close)
	if _, e = db.Exec(ctx, strings.ReplaceAll(schema, "kimcloud", name)); e != nil {
		t.Fatal(e)
	}
	if _, e = db.Exec(ctx, strings.ReplaceAll(sessionSchema, "kimcloud", name)); e != nil {
		t.Fatal(e)
	}
	if _, e = db.Exec(ctx, strings.ReplaceAll(eventSchema, "kimcloud", name)); e != nil {
		t.Fatal(e)
	}
	a := newApp(Config{Development: true, PublicOrigin: "https://notes.test"}, db)
	s := httptest.NewServer(a.routes())
	t.Cleanup(s.Close)
	return &testEnv{a, s, db, t}
}
func (e *testEnv) request(method, path string, body any, cookie *http.Cookie) (int, map[string]any, *http.Response) {
	e.t.Helper()
	var data io.Reader
	if body != nil {
		raw, _ := json.Marshal(body)
		data = bytes.NewReader(raw)
	}
	r, _ := http.NewRequest(method, e.server.URL+"/api/v1/apps/"+path, data)
	r.Header.Set("Origin", "https://notes.test")
	r.Header.Set("Content-Type", "application/json")
	if cookie != nil {
		r.AddCookie(cookie)
	}
	resp, err := http.DefaultClient.Do(r)
	if err != nil {
		e.t.Fatal(err)
	}
	defer resp.Body.Close()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	return resp.StatusCode, out, resp
}
func (e *testEnv) account(label string) *http.Cookie {
	e.t.Helper()
	status, out, resp := e.request("POST", "kimnotes/auth/register", map[string]any{"email": label + "@example.com", "password": "Test-password-123"}, nil)
	if status != 200 {
		e.t.Fatalf("register %d %#v", status, out)
	}
	for _, c := range resp.Cookies() {
		if c.Name == "kim_session_kimnotes" {
			return c
		}
	}
	e.t.Fatal("no cookie")
	return nil
}
func (e *testEnv) push(c *http.Cookie, op Operation) map[string]any {
	e.t.Helper()
	status, out, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": []Operation{op}}, c)
	if status != 200 {
		e.t.Fatalf("push %d %#v", status, out)
	}
	return out
}
func result(out map[string]any) map[string]any { return out["results"].([]any)[0].(map[string]any) }
func TestAccountsAndSync(t *testing.T) {
	e := newTestEnv(t)
	one := e.account("one")
	two := e.account("two")
	if status, _, _ := e.request("GET", "kimnotes/auth/me", nil, nil); status != 401 {
		t.Fatal("anonymous authenticated")
	}
	if status, _, _ := e.request("POST", "kimnotes/auth/login", credentials{Email: "one@example.com", Password: "wrong"}, nil); status != 401 {
		t.Fatal("wrong password")
	}
	if _, err := e.db.Exec(context.Background(), "INSERT INTO applications(id,name) VALUES('another-app','Another')"); err != nil {
		t.Fatal(err)
	}
	status, auth, resp := e.request("POST", "another-app/auth/login", credentials{Email: "one@example.com", Password: "Test-password-123"}, nil)
	if status != 200 {
		t.Fatal(status, auth)
	}
	var secondApp *http.Cookie
	for _, c := range resp.Cookies() {
		if strings.Contains(c.Name, "another-app") {
			secondApp = c
		}
	}
	if status, _, _ := e.request("GET", "another-app/auth/me", nil, one); status != 401 {
		t.Fatal("cross-app session accepted")
	}
	id := newID()
	op := Operation{OperationID: newID(), NoteID: id, Action: "upsert", Document: &Document{Title: "<img src=x onerror=alert(1)>", Text: "hello 世界", ThemeID: 2}}
	first := e.push(one, op)
	if result(first)["status"] != "applied" {
		t.Fatal(first)
	}
	again := e.push(one, op)
	if result(again)["note"].(map[string]any)["revision"] != float64(1) {
		t.Fatal("duplicate not idempotent")
	}
	reused := op
	d := *op.Document
	d.Text = "changed"
	reused.Document = &d
	if s, _, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": []Operation{reused}}, one); s != 409 {
		t.Fatal("reused operation accepted")
	}
	if s, o, _ := e.request("GET", "kimnotes/sync/pull?cursor=0", nil, two); s != 200 || len(o["changes"].([]any)) != 0 {
		t.Fatal("cross-user leak", s, o)
	}
	if s, o, _ := e.request("GET", "another-app/sync/pull?cursor=0", nil, secondApp); s != 200 || len(o["changes"].([]any)) != 0 {
		t.Fatal("cross-app leak", s, o)
	}
	op.OperationID = newID()
	op.BaseRevision = 0
	conflict := e.push(one, op)
	if result(conflict)["status"] != "conflict_copy" {
		t.Fatal("conflict overwritten", conflict)
	}
	if result(conflict)["note"].(map[string]any)["id"] == id {
		t.Fatal("missing conflict copy")
	}
	del := Operation{OperationID: newID(), NoteID: id, BaseRevision: 1, Action: "delete"}
	e.push(one, del)
	restore := Operation{OperationID: newID(), NoteID: id, BaseRevision: 2, Action: "restore"}
	restored := e.push(one, restore)
	if result(restored)["note"].(map[string]any)["deletedAt"] != nil {
		t.Fatal("restore failed")
	}
	s, history, _ := e.request("GET", "kimnotes/notes/"+id+"/versions", nil, one)
	if s != 200 || len(history["versions"].([]any)) != 3 {
		t.Fatal("version history", s, history)
	}
	if s, _, _ := e.request("GET", "kimnotes/notes/"+id+"/versions", nil, two); s != 404 {
		t.Fatal("history leak")
	}
	s, page, _ := e.request("GET", "kimnotes/sync/pull?cursor=0&limit=2", nil, one)
	if s != 200 || page["hasMore"] != true || page["cursor"] != "2" {
		t.Fatal("paging", s, page)
	}
	// Browser credentials require a matching origin even when the cookie is valid.
	r, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/kimnotes/auth/logout", nil)
	r.AddCookie(one)
	r.Header.Set("Origin", "https://attacker.test")
	res, err := http.DefaultClient.Do(r)
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	if res.StatusCode != 403 {
		t.Fatal("CSRF accepted")
	}
	if s, _, _ := e.request("POST", "kimnotes/auth/logout", map[string]any{}, one); s != 200 {
		t.Fatal("logout")
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, one); s != 401 {
		t.Fatal("revoked session accepted")
	}
}
func TestConcurrentStream(t *testing.T) {
	e := newTestEnv(t)
	c := e.account("concurrent")
	var wg sync.WaitGroup
	errors := make(chan string, 12)
	for i := 0; i < 12; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			op := Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Title: fmt.Sprint(i), Text: "concurrent"}}
			raw, _ := json.Marshal(map[string]any{"operations": []Operation{op}})
			r, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/kimnotes/sync/push", bytes.NewReader(raw))
			r.Header.Set("Origin", "https://notes.test")
			r.Header.Set("Content-Type", "application/json")
			r.AddCookie(c)
			resp, err := http.DefaultClient.Do(r)
			if err != nil {
				errors <- err.Error()
				return
			}
			resp.Body.Close()
			if resp.StatusCode != 200 {
				errors <- fmt.Sprint(resp.StatusCode)
			}
		}(i)
	}
	wg.Wait()
	close(errors)
	for err := range errors {
		t.Error(err)
	}
	s, out, _ := e.request("GET", "kimnotes/sync/pull?cursor=0", nil, c)
	if s != 200 || out["cursor"] != "12" || len(out["changes"].([]any)) != 12 {
		t.Fatal("commit gap", s, out)
	}
}
func TestAttachments(t *testing.T) {
	e := newTestEnv(t)
	one := e.account("images")
	two := e.account("private")
	var buf bytes.Buffer
	_ = png.Encode(&buf, image.NewRGBA(image.Rect(0, 0, 2, 3)))
	upload := func(data []byte) (int, map[string]any) {
		r, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/kimnotes/attachments", bytes.NewReader(data))
		r.Header.Set("Origin", "https://notes.test")
		r.Header.Set("Content-Type", "image/png")
		r.AddCookie(one)
		res, err := http.DefaultClient.Do(r)
		if err != nil {
			t.Fatal(err)
		}
		defer res.Body.Close()
		var out map[string]any
		_ = json.NewDecoder(res.Body).Decode(&out)
		return res.StatusCode, out
	}
	if s, _ := upload([]byte(`<svg onload="alert(1)"></svg>`)); s != 400 {
		t.Fatal("SVG accepted")
	}
	s, img := upload(buf.Bytes())
	if s != 201 {
		t.Fatal(s, img)
	}
	id := img["id"].(string)
	if s, _, _ := e.request("GET", "kimnotes/attachments/"+id, nil, two); s != 404 {
		t.Fatal("private attachment leaked")
	}
	if s, _, _ := e.request("GET", "kimnotes/attachments/"+id, nil, one); s != 200 {
		t.Fatal("own image not readable")
	}
	e.push(one, Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Title: "image", Attachments: []string{id}}})
	foreign := Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Title: "image", Attachments: []string{id}}}
	if s, _, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": []Operation{foreign}}, two); s != 400 {
		t.Fatal("foreign attachment reference accepted")
	}
}
func TestVerificationAndReset(t *testing.T) {
	e := newTestEnv(t)
	e.app.config.RequireVerification = true
	e.app.config.SMTPHost = "smtp.test"
	e.app.config.MailFrom = "notes@example.com"
	var body string
	e.app.sendMail = func(_ context.Context, _, _, text string) error { body = text; return nil }
	s, out, _ := e.request("POST", "kimnotes/auth/register", credentials{Email: "verify@example.com", Password: "Test-password-123"}, nil)
	if s != 202 {
		t.Fatal(s, out)
	}
	id := out["challengeId"].(string)
	code := strings.Split(strings.Split(body, "：")[1], "\r")[0]
	if s, _, _ := e.request("POST", "kimnotes/auth/verify", map[string]string{"challengeId": id, "code": "000xxx"}, nil); s != 400 {
		t.Fatal("bad code")
	}
	s, out, res := e.request("POST", "kimnotes/auth/verify", map[string]string{"challengeId": id, "code": code}, nil)
	if s != 200 {
		t.Fatal(s, out)
	}
	c := res.Cookies()[0]
	if s, _, _ := e.request("POST", "kimnotes/auth/verify", map[string]string{"challengeId": id, "code": code}, nil); s != 400 {
		t.Fatal("code replay")
	}
	if s, _, _ := e.request("POST", "kimnotes/auth/reset", map[string]string{"email": "verify@example.com"}, nil); s != 202 {
		t.Fatal("reset mail")
	}
	fragment := strings.Split(strings.Split(body, "#")[1], "\r")[0]
	parts := strings.Split(fragment, ":")
	s, out, _ = e.request("POST", "kimnotes/auth/reset/complete", map[string]string{"challengeId": parts[0], "token": parts[1], "password": "New-password-456"}, nil)
	if s != 200 {
		t.Fatal(s, out)
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, c); s != 401 {
		t.Fatal("reset failed to revoke sessions")
	}
	if s, _, _ := e.request("POST", "kimnotes/auth/login", credentials{Email: "verify@example.com", Password: "Test-password-123"}, nil); s != 401 {
		t.Fatal("old password accepted")
	}
	if s, _, _ := e.request("POST", "kimnotes/auth/login", credentials{Email: "verify@example.com", Password: "New-password-456"}, nil); s != 200 {
		t.Fatal("new password rejected")
	}
}
func TestRateLimit(t *testing.T) {
	e := newTestEnv(t)
	r := httptest.NewRequest("POST", "/", nil)
	for i := 0; i < 3; i++ {
		if err := e.app.limited(r, "limit-test", 2, time.Minute); (i < 2) != (err == nil) {
			t.Fatal("limit", i, err)
		}
	}
}

func TestDesktopAndGateway(t *testing.T) {
	e := newTestEnv(t)
	_ = e.account("desktop")
	raw, _ := json.Marshal(credentials{Email: "DESKTOP@example.com", Password: "Test-password-123", DeviceName: "Regression PC"})
	r, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/kimnotes/auth/login", bytes.NewReader(raw))
	r.Header.Set("X-Kim-Client", "desktop")
	r.Header.Set("Content-Type", "application/json")
	res, err := http.DefaultClient.Do(r)
	if err != nil {
		t.Fatal(err)
	}
	var auth map[string]any
	_ = json.NewDecoder(res.Body).Decode(&auth)
	res.Body.Close()
	if res.StatusCode != 200 {
		t.Fatal(res.StatusCode, auth)
	}
	token, ok := auth["token"].(string)
	if !ok || len(token) != 43 || len(res.Cookies()) != 0 {
		t.Fatal("desktop session transport")
	}
	r, _ = http.NewRequest("GET", e.server.URL+"/api/v1/apps/kimnotes/auth/me", nil)
	r.Header.Set("Authorization", "Bearer "+token)
	res, err = http.DefaultClient.Do(r)
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	if res.StatusCode != 200 {
		t.Fatal("bearer rejected")
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, &http.Cookie{Name: "kim_session_kimnotes", Value: token}); s != 401 {
		t.Fatal("desktop token accepted as web session")
	}
	e.app.config.Development = false
	e.app.config.GatewaySecret = strings.Repeat("k", 40)
	if s, _, _ := e.request("GET", "kimnotes/config", nil, nil); s != 403 {
		t.Fatal("direct origin access accepted")
	}
	r, _ = http.NewRequest("GET", e.server.URL+"/api/v1/apps/kimnotes/config", nil)
	r.Header.Set("X-Kim-Gateway", e.app.config.GatewaySecret)
	res, err = http.DefaultClient.Do(r)
	if err != nil {
		t.Fatal(err)
	}
	res.Body.Close()
	if res.StatusCode != 200 {
		t.Fatal("trusted gateway rejected")
	}
}

func TestLargeBatchRollback(t *testing.T) {
	e := newTestEnv(t)
	c := e.account("large-batch")
	id := newID()
	e.push(c, Operation{OperationID: newID(), NoteID: id, Action: "upsert", Document: &Document{Title: "large document", RTF: "{\\rtf1\\ansi " + strings.Repeat("x", 1800000) + "}"}})
	ops := []Operation{}
	for i := 0; i < 5; i++ {
		ops = append(ops, Operation{OperationID: newID(), NoteID: id, BaseRevision: 1, Action: "delete"})
	}
	status, out, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": ops}, c)
	if status != 413 || out["code"] != "response_limit" {
		t.Fatal("large response was not bounded", status, out["code"])
	}
	status, out, _ = e.request("GET", "kimnotes/sync/pull?cursor=0", nil, c)
	if status != 200 || len(out["changes"].([]any)) != 1 {
		t.Fatal("failed batch partially committed")
	}
	note := out["changes"].([]any)[0].(map[string]any)
	if note["revision"] != float64(1) || note["deletedAt"] != nil {
		t.Fatal("failed batch deleted original")
	}
}
