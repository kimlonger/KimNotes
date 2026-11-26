package main

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"strings"
	"testing"
	"time"
)

const handoffPath = "kimnotes/auth/browser-handoff"

func (e *testEnv) mintHandoff(pair map[string]any, verifier string) string {
	e.t.Helper()
	challenge := sha256.Sum256([]byte(verifier))
	status, out, response := e.sessionRequest("POST", handoffPath,
		map[string]string{"challenge": base64.RawURLEncoding.EncodeToString(challenge[:])}, nil,
		pair["token"].(string), map[string]string{"X-Kim-Client": "desktop"})
	code, _ := out["code"].(string)
	if status != 200 || len(response.Cookies()) != 0 || out["expiresIn"] != float64(90) || decodeHandoffToken(code) == nil {
		e.t.Fatalf("desktop did not receive a short-lived handoff: %d", status)
	}
	var remaining float64
	err := e.db.QueryRow(context.Background(), `SELECT extract(epoch FROM expires_at-clock_timestamp())::float8
		FROM note_browser_handoffs WHERE code_hash=$1 AND challenge=$2 AND source_session_id=$3`,
		digest(code), challenge[:], pair["sessionId"]).Scan(&remaining)
	if err != nil || remaining <= 0 || remaining > 90 {
		e.t.Fatal("handoff expiry, proof, or source session differs from the grant", err)
	}
	return code
}

func TestBrowserHandoffSwitchAndReplay(t *testing.T) {
	e := newTestEnv(t)
	e.account("handoff-a")
	b := e.account("handoff-b")
	a := e.desktop("handoff-a", true)
	verifier := randomToken()
	code := e.mintHandoff(a, verifier)
	body := map[string]string{"code": code, "verifier": verifier}
	consume := handoffPath + "/consume"
	// An invalid proof cannot replace an existing browser identity or burn a valid grant.
	bad := map[string]string{"code": code, "verifier": randomToken()}
	status, _, response := e.request("POST", consume, bad, b)
	if status != 401 || len(response.Cookies()) != 0 {
		t.Fatal("invalid browser proof affected existing cookie", status)
	}
	status, out, response := e.request("POST", consume, body, b)
	if status != 200 || out["user"].(map[string]any)["id"] != a["user"].(map[string]any)["id"] ||
		out["sessionId"] == a["sessionId"] || out["token"] != nil || out["refreshToken"] != nil {
		t.Fatal("browser did not receive its own session for A", status)
	}
	cookies := response.Cookies()
	if len(cookies) != 1 || cookies[0].Name != b.Name || cookies[0].Value == b.Value || !cookies[0].HttpOnly ||
		cookies[0].SameSite != http.SameSiteLaxMode || cookies[0].Path != "/" {
		t.Fatal("web cookie did not replace B using the standard cookie policy")
	}
	status, me, _ := e.request("GET", "kimnotes/auth/me", nil, cookies[0])
	if status != 200 || me["user"].(map[string]any)["email"] != "handoff-a@example.com" {
		t.Fatal("replacement cookie did not authenticate as A", status)
	}
	if status, _, response = e.request("POST", consume, body, b); status != 401 || len(response.Cookies()) != 0 {
		t.Fatal("spent handoff accepted", status)
	}
	if ok, err := e.app.revokeDevice(context.Background(), a["user"].(map[string]any)["id"].(string), a["sessionId"].(string), "test"); !ok || err != nil {
		t.Fatal(err)
	}
	if status, _, _ = e.request("GET", "kimnotes/auth/me", nil, cookies[0]); status != 200 {
		t.Fatal("web login did not get its own lifecycle", status)
	}
}

func TestBrowserHandoffScopeAndSourceValidity(t *testing.T) {
	e := newTestEnv(t)
	web := e.account("handoff-scope")
	a := e.desktop("handoff-scope", true)
	verifier := randomToken()
	proof := map[string]string{"challenge": randomToken()}
	if status, _, _ := e.request("POST", handoffPath, proof, web); status != 403 {
		t.Fatal("web session minted desktop grant", status)
	}
	if status, _, _ := e.sessionRequest("POST", handoffPath, map[string]string{"challenge": strings.Repeat("a", 43)}, nil,
		a["token"].(string), map[string]string{"X-Kim-Client": "desktop"}); status != 400 {
		t.Fatal("noncanonical proof accepted", status)
	}
	code := e.mintHandoff(a, verifier)
	body := map[string]string{"code": code, "verifier": verifier}
	consume := handoffPath + "/consume"
	for _, headers := range []map[string]string{{"Origin": ""}, {"Origin": "https://elsewhere.test"}, {"X-Kim-Client": "desktop"}, {"Sec-Fetch-Site": "cross-site"}} {
		if status, _, response := e.sessionRequest("POST", consume, body, nil, "", headers); status != 403 || len(response.Cookies()) != 0 {
			t.Fatal("non-browser/same-origin request succeeded", status)
		}
	}
	if status, _, _ := e.sessionRequest("POST", consume, body, nil, a["token"].(string), nil); status != 403 {
		t.Fatal("bearer exchanged as web session", status)
	}
	if status, _, _ := e.request("POST", "guitar/auth/browser-handoff/consume", body, nil); status != 404 {
		t.Fatal("different app exchanged notes handoff", status)
	}
	e.sql("INSERT INTO applications(id,name) VALUES('another-app','another')")
	status, other, _ := e.sessionRequest("POST", "another-app/auth/login", credentials{Email: "handoff-scope@example.com", Password: "Test-password-123"}, nil, "",
		map[string]string{"X-Kim-Client": "desktop", "X-Kim-Auth-Version": "2"})
	if status != 200 {
		t.Fatal("could not establish different app session", status)
	}
	if status, _, _ = e.sessionRequest("POST", "another-app/auth/browser-handoff", proof, nil, other["token"].(string), map[string]string{"X-Kim-Client": "desktop"}); status != 404 {
		t.Fatal("different app minted handoff", status)
	}
	e.sql("UPDATE note_browser_handoffs SET expires_at=now()-interval '1 second' WHERE code_hash=$1", digest(code))
	if status, _, _ := e.request("POST", consume, body, web); status != 401 {
		t.Fatal("expired grant accepted", status)
	}
	if err := e.app.cleanupSessionCaches(context.Background()); err != nil {
		t.Fatal(err)
	}
	var stale int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM note_browser_handoffs").Scan(&stale); err != nil || stale != 0 {
		t.Fatal("expired tickets retained", err)
	}
	for _, invalidate := range []string{
		"UPDATE sessions SET expires_at=now()-interval '1 second' WHERE id=$1",
		"UPDATE sessions SET revoked_at=now() WHERE id=$1",
		"UPDATE users SET auth_version=auth_version+1 WHERE id=(SELECT user_id FROM sessions WHERE id=$1)",
	} {
		a = e.desktop("handoff-scope", true)
		body["code"] = e.mintHandoff(a, verifier)
		e.sql(invalidate, a["sessionId"])
		if status, _, res := e.request("POST", consume, body, web); status != 401 || len(res.Cookies()) != 0 {
			t.Fatal("invalid source still gave a browser session", status)
		}
	}
}

func TestBrowserHandoffConcurrentConsume(t *testing.T) {
	e := newTestEnv(t)
	e.account("handoff-concurrent")
	a := e.desktop("handoff-concurrent", true)
	verifier := randomToken()
	body := map[string]string{"code": e.mintHandoff(a, verifier), "verifier": verifier}
	const clients = 6
	start, results := make(chan struct{}), make(chan int, clients)
	for i := 0; i < clients; i++ {
		go func() {
			<-start
			status, _, _ := e.sessionRequest("POST", handoffPath+"/consume", body, nil, "", nil)
			results <- status
		}()
	}
	close(start)
	success := 0
	for i := 0; i < clients; i++ {
		select {
		case status := <-results:
			switch status {
			case 200:
				success++
			case 401:
			default:
				t.Fatalf("unexpected concurrent exchange status %d", status)
			}
		case <-time.After(15 * time.Second):
			t.Fatal("concurrent exchanges did not finish")
		}
	}
	if success != 1 {
		t.Fatalf("a single grant created %d web sessions", success)
	}
}
