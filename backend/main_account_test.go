package main

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestMainProjectConfiguration(t *testing.T) {
	ids, err := mainProjectIDs("")
	if err != nil || len(ids) != 0 {
		t.Fatal("default must use the published database catalogue", ids, err)
	}
	ids, err = mainProjectIDs("notes, future-plugin")
	if err != nil || len(ids) != 2 || ids[1] != "future-plugin" {
		t.Fatal("configured catalogue cannot grow", ids, err)
	}
	for _, invalid := range []string{"notes,notes", "notes,", "Notes", "../notes", strings.Repeat("x", 49)} {
		if _, err := mainProjectIDs(invalid); err == nil {
			t.Fatal("invalid project configuration accepted", invalid)
		}
	}
	for _, action := range []string{"open", "download", "source", "details"} {
		if !validEntryAction(action) {
			t.Fatal("valid entry action rejected", action)
		}
	}
	if validEntryAction("app-used") || validEntryAction("") {
		t.Fatal("site entries must not pretend to be in-app usage")
	}
}

func TestRegisteredApplicationOriginValidation(t *testing.T) {
	for _, origin := range []string{"https://kimlulu.com", "https://note.kimlulu.com", "https://example.com:8443"} {
		if !validApplicationOrigin(origin) {
			t.Fatal("valid registered origin rejected", origin)
		}
	}
	for _, origin := range []string{"", "http://kimlulu.com", "https://user:password@kimlulu.com", "https://kimlulu.com/", "https://kimlulu.com/login", "https://kimlulu.com?next=elsewhere", "https://kimlulu.com?", "https://kimlulu.com#", "https://kimlulu.com#fragment", "//kimlulu.com", "https:///kimlulu.com"} {
		if validApplicationOrigin(origin) {
			t.Fatal("unsafe registered mail origin accepted", origin)
		}
	}
}

type mainTestClient struct {
	cookie *http.Cookie
	user   string
}

func (e *testEnv) mainLogin(label string) mainTestClient {
	e.t.Helper()
	s, out, res := e.request("POST", "main/auth/login", credentials{Email: label + "@example.com", Password: "Test-password-123"}, nil)
	if s != 200 {
		e.t.Fatalf("shared account main login %d %v", s, out)
	}
	for _, c := range res.Cookies() {
		if c.Name == "kim_session_main" {
			return mainTestClient{c, out["user"].(map[string]any)["id"].(string)}
		}
	}
	e.t.Fatal("main cookie missing")
	return mainTestClient{}
}

func (e *testEnv) mainRequest(method, path string, body any, c mainTestClient) (int, map[string]any) {
	e.t.Helper()
	s, out, _ := e.sessionRequest(method, "main/account/"+path, body, c.cookie, "", map[string]string{"X-Kim-Expected-User": c.user})
	return s, out
}

func TestMainAccountUsesOneIdentityAndIsolatesData(t *testing.T) {
	e := newTestEnv(t)
	note := e.account("shared-main")
	one := e.mainLogin("shared-main")
	_, me, _ := e.request("GET", "kimnotes/auth/me", nil, note)
	if me["user"].(map[string]any)["id"] != one.user {
		t.Fatal("main login created a second identity")
	}
	var users, apps int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM users WHERE email='shared-main@example.com'").Scan(&users); err != nil || users != 1 {
		t.Fatal("duplicate identity", users, err)
	}
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM user_apps WHERE user_id=$1", one.user).Scan(&apps); err != nil || apps != 2 {
		t.Fatal("shared user app memberships", apps, err)
	}
	if s, _, _ := e.request("POST", "main/auth/register", credentials{Email: "shared-main@example.com", Password: "Test-password-123"}, nil); s != 409 {
		t.Fatal("same email registered a second app identity", s)
	}
	if s, out := e.mainRequest("GET", "follows", nil, one); s != 200 || len(out["projectIds"].([]any)) != 0 || out["updatedAt"] != nil {
		t.Fatal("new account is not empty", s, out)
	}
	if s, out := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{"notes", "wincodex"}}, one); s != 200 || out["updatedAt"] == nil {
		t.Fatal("cannot save real follows", s, out)
	}
	secondSession := e.mainLogin("shared-main")
	if s, out := e.mainRequest("GET", "follows", nil, secondSession); s != 200 || len(out["projectIds"].([]any)) != 2 {
		t.Fatal("follows were not shared across sessions", s, out)
	}
	e.account("other-main")
	two := e.mainLogin("other-main")
	if s, out := e.mainRequest("GET", "follows?userId="+one.user, nil, two); s != 200 || len(out["projectIds"].([]any)) != 0 {
		t.Fatal("query user ID leaked follows", s, out)
	}
	for _, path := range []string{"follows", "overview", "notes-summary"} {
		if s, _, _ := e.request("GET", "main/account/"+path, nil, nil); s != 401 {
			t.Fatal("anonymous account read", path, s)
		}
		if s, _, _ := e.request("GET", "main/account/"+path, nil, note); s != 401 {
			t.Fatal("cross-app cookie accepted", path, s)
		}
		if s, _, _ := e.sessionRequest("GET", "kimnotes/account/"+path, nil, note, "", map[string]string{"X-Kim-Expected-User": one.user}); s != 404 {
			t.Fatal("note app reached main account capability", path, s)
		}
	}
	if s, _, _ := e.request("GET", "main/account/follows", nil, one.cookie); s != 409 {
		t.Fatal("missing stable account identity accepted", s)
	}
	if s, _, _ := e.sessionRequest("PUT", "main/account/follows", map[string]any{"projectIds": []string{}}, one.cookie, "", map[string]string{"X-Kim-Expected-User": two.user}); s != 409 {
		t.Fatal("cross-tab account mismatch wrote data", s)
	}
	if s, _, _ := e.sessionRequest("PUT", "main/account/follows", map[string]any{"projectIds": []string{}}, one.cookie, "", map[string]string{"X-Kim-Expected-User": one.user, "Origin": "https://outside.invalid"}); s != 403 {
		t.Fatal("untrusted origin wrote data", s)
	}
	for _, body := range []any{
		map[string]any{"projectIds": []string{"other"}},
		map[string]any{"projectIds": []string{"notes", "notes"}},
		map[string]any{"projectIds": nil}, map[string]any{},
		map[string]any{"projectIds": []string{}, "userId": two.user},
	} {
		if s, _ := e.mainRequest("PUT", "follows", body, one); s != 400 {
			t.Fatal("invalid follows accepted", body, s)
		}
	}
	if s, out := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{}}, one); s != 200 || len(out["projectIds"].([]any)) != 0 {
		t.Fatal("clear follows", s, out)
	}
}

func TestMainEntriesBoundHistoryAndKeepLifetimeCounts(t *testing.T) {
	e := newTestEnv(t)
	e.account("entries-main")
	one := e.mainLogin("entries-main")
	for i := 0; i < retainedAccountEntries+5; i++ {
		project := "notes"
		if i%2 == 0 {
			project = "wincodex"
		}
		if s, out := e.mainRequest("POST", "entries", map[string]string{"projectId": project, "action": "open"}, one); s != 201 || out["recorded"] != true || out["entry"].(map[string]any)["createdAt"] == nil {
			t.Fatal("entry not recorded", i, s, out)
		}
	}
	var retained int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM main_account_entries WHERE user_id=$1", one.user).Scan(&retained); err != nil || retained != retainedAccountEntries {
		t.Fatal("unbounded event storage", retained, err)
	}
	s, out := e.mainRequest("GET", "overview", nil, one)
	if s != 200 {
		t.Fatal(s, out)
	}
	activity := out["activity"].(map[string]any)
	if activity["total"] != float64(105) || activity["kind"] != "site_entries" || len(activity["recent"].([]any)) != 20 {
		t.Fatal("lifetime counts changed when old events were trimmed", out)
	}
	counts := activity["byProject"].(map[string]any)
	if counts["wincodex"] != float64(53) || counts["notes"] != float64(52) {
		t.Fatal("project totals", counts)
	}
	for _, body := range []any{
		map[string]string{"projectId": "notes", "action": "used"},
		map[string]string{"projectId": "unknown", "action": "open"},
		map[string]string{"projectId": "notes", "action": "open", "userId": newID()},
	} {
		if s, _ := e.mainRequest("POST", "entries", body, one); s != 400 {
			t.Fatal("invalid activity accepted", s, body)
		}
	}
	e.account("entries-other")
	two := e.mainLogin("entries-other")
	if s, out := e.mainRequest("GET", "overview?userId="+one.user, nil, two); s != 200 || out["activity"].(map[string]any)["total"] != float64(0) {
		t.Fatal("activity crossed accounts", s, out)
	}
}

func TestMainConcurrentEntriesAndRateLimits(t *testing.T) {
	e := newTestEnv(t)
	e.account("concurrent-main")
	one := e.mainLogin("concurrent-main")
	var group sync.WaitGroup
	for i := 0; i < 10; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			if s, out := e.mainRequest("POST", "entries", map[string]string{"projectId": "guitar", "action": "details"}, one); s != 201 {
				t.Errorf("concurrent entry %d %v", s, out)
			}
		}()
	}
	group.Wait()
	if s, out := e.mainRequest("GET", "overview", nil, one); s != 200 || out["activity"].(map[string]any)["total"] != float64(10) {
		t.Fatal("concurrent counts lost", s, out)
	}
	for _, limit := range []struct {
		key, method, path string
		max               int
		body              any
	}{
		{"main:entries:", "POST", "entries", 120, map[string]string{"projectId": "notes", "action": "download"}},
		{"main:follows:", "PUT", "follows", 30, map[string]any{"projectIds": []string{"notes"}}},
		{"main:read:", "GET", "overview", 90, nil},
	} {
		e.sql("INSERT INTO rate_limits(key,window_start,count) VALUES($1,now(),$2) ON CONFLICT(key) DO UPDATE SET window_start=now(),count=EXCLUDED.count", digest(limit.key+one.user), limit.max)
		if s, _ := e.mainRequest(limit.method, limit.path, limit.body, one); s != 429 {
			t.Fatal("central account rate limit not enforced", limit.key, s)
		}
	}
}

func TestMainNotesSummaryIsPrivateSameUserAggregate(t *testing.T) {
	e := newTestEnv(t)
	e.account("summary-main")
	one := e.mainLogin("summary-main")
	e.account("summary-other")
	two := e.mainLogin("summary-other")
	insert := func(app, user string, document map[string]any, deleted bool) {
		t.Helper()
		raw, _ := json.Marshal(document)
		var when *time.Time
		if deleted {
			now := time.Now()
			when = &now
		}
		e.sql("INSERT INTO notes(app_id,user_id,id,revision,document,deleted_at) VALUES($1,$2,$3,1,$4,$5)", app, user, newID(), raw, when)
	}
	insert("kimnotes", one.user, map[string]any{"title": "PRIVATE_TITLE", "text": "PRIVATE_BODY", "attachments": []string{}}, false)
	insert("kimnotes", one.user, map[string]any{"title": "PRIVATE_IMAGE", "attachments": []string{newID()}}, false)
	insert("kimnotes", one.user, map[string]any{"text": "DELETED_PRIVATE"}, true)
	insert("main", one.user, map[string]any{"text": "WRONG_APP_PRIVATE"}, false)
	insert("kimnotes", two.user, map[string]any{"text": "OTHER_USER_PRIVATE"}, false)
	s, out := e.mainRequest("GET", "notes-summary?userId="+two.user, nil, one)
	if s != 200 || out["noteCount"] != float64(2) || out["imageNoteCount"] != float64(1) || out["countIsExact"] != true || out["lastUpdatedAt"] == nil || out["checkedAt"] == nil || out["website"] != "https://note.kimlulu.com/notes/" {
		t.Fatal("same-user aggregate incorrect", s, out)
	}
	raw, _ := json.Marshal(out)
	if strings.Contains(string(raw), "PRIVATE") || strings.Contains(string(raw), "document") || strings.Contains(string(raw), "attachment") || strings.Contains(string(raw), "title") {
		t.Fatal("private note contents escaped aggregate API", string(raw))
	}
	insert("kimnotes", one.user, map[string]any{"rtfGzip": packedRTF(`{\rtf1\pict old image}`)}, false)
	if s, out = e.mainRequest("GET", "notes-summary", nil, one); s != 200 || out["noteCount"] != float64(3) || out["countIsExact"] != false {
		t.Fatal("legacy image uncertainty hidden", s, out)
	}
	if s, out = e.mainRequest("GET", "notes-summary", nil, two); s != 200 || out["noteCount"] != float64(1) {
		t.Fatal("summary crossed accounts", s, out)
	}
}

func TestMainMigrationIsAdditiveAndIdempotent(t *testing.T) {
	e := newTestEnv(t)
	note := e.account("migration-main")
	id := newID()
	e.push(note, Operation{OperationID: newID(), NoteID: id, Action: "upsert", Document: &Document{Title: "preserve", Text: "unchanged"}})
	var currentSchema string
	if err := e.db.QueryRow(context.Background(), "SELECT current_schema()").Scan(&currentSchema); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		e.sql(strings.ReplaceAll(mainAccountSchema, "kimcloud", currentSchema))
	}
	if s, out, _ := e.request("GET", "kimnotes/auth/me", nil, note); s != 200 {
		t.Fatal("migration invalidated existing note session", s, out)
	}
	if s, out, _ := e.request("GET", "kimnotes/sync/pull?cursor=0", nil, note); s != 200 || len(out["changes"].([]any)) != 1 {
		t.Fatal("migration changed existing notes", s, out)
	}
	one := e.mainLogin("migration-main")
	if s, _ := e.mainRequest("PUT", "follows", map[string]any{"projectIds": []string{"notes"}}, one); s != 200 {
		t.Fatal("new app registration unavailable", s)
	}
}

func TestResetMailUsesRegisteredApplicationOrigin(t *testing.T) {
	e := newTestEnv(t)
	noteCookie := e.account("reset-shared")
	e.app.config.PublicOrigin = "https://note.kimlulu.com"
	e.app.config.SMTPHost, e.app.config.MailFrom = "smtp.invalid", "test@example.com"
	var messages []string
	e.app.sendMail = func(_ context.Context, _, _, body string) error {
		messages = append(messages, body)
		return nil
	}
	for _, app := range []string{"main", "kimnotes"} {
		s, out, _ := e.sessionRequest("POST", app+"/auth/reset", map[string]string{"email": "reset-shared@example.com"}, nil, "", map[string]string{"Origin": "https://note.kimlulu.com"})
		if s != 202 || len(messages) == 0 {
			t.Fatal("mock reset mail not created", app, s, out)
		}
		want := "https://kimlulu.com/login/?mode=reset#"
		if app == "kimnotes" {
			want = "https://note.kimlulu.com/login/?mode=reset#"
		}
		if !strings.Contains(messages[len(messages)-1], want) {
			t.Fatal("reset mail landed on wrong registered app", app)
		}
	}
	// A main challenge must complete on main, while changing the one shared
	// account password also invalidates its existing notes session.
	link := strings.Split(strings.Split(messages[0], "#")[1], "\r\n")[0]
	pair := strings.SplitN(link, ":", 2)
	complete := map[string]string{"challengeId": pair[0], "token": pair[1], "password": "New-shared-password-456"}
	if s, _, _ := e.sessionRequest("POST", "kimnotes/auth/reset/complete", complete, nil, "", map[string]string{"Origin": "https://note.kimlulu.com"}); s != 400 {
		t.Fatal("main reset challenge crossed applications", s)
	}
	if s, _, _ := e.sessionRequest("POST", "main/auth/reset/complete", complete, nil, "", map[string]string{"Origin": "https://note.kimlulu.com"}); s != 200 {
		t.Fatal("main reset challenge could not complete", s)
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, noteCookie); s != 401 {
		t.Fatal("main password reset did not revoke shared-account notes session", s)
	}
	if s, _, _ := e.sessionRequest("POST", "main/auth/reset", map[string]string{"email": "reset-shared@example.com", "origin": "https://outside.invalid"}, nil, "", map[string]string{"Origin": "https://note.kimlulu.com"}); s != 400 {
		t.Fatal("request supplied mail destination accepted", s)
	}
	e.sql("UPDATE applications SET public_origin='https://attacker@example.com' WHERE id='main'")
	before := len(messages)
	if s, _, _ := e.sessionRequest("POST", "main/auth/reset", map[string]string{"email": "reset-shared@example.com"}, nil, "", map[string]string{"Origin": "https://note.kimlulu.com"}); s != 503 || len(messages) != before {
		t.Fatal("invalid registered origin sent mail", s)
	}
}
