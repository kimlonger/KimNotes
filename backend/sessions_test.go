package main

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"
)

func (e *testEnv) sessionRequest(method, path string, body any, cookie *http.Cookie, token string, headers map[string]string) (int, map[string]any, *http.Response) {
	e.t.Helper()
	raw, _ := json.Marshal(body)
	req, _ := http.NewRequest(method, e.server.URL+"/api/v1/apps/"+path, bytes.NewReader(raw))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Origin", "https://notes.test")
	if cookie != nil {
		req.AddCookie(cookie)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		e.t.Errorf("HTTP request failed: %v", err)
		return 0, nil, nil
	}
	defer res.Body.Close()
	out := map[string]any{}
	_ = json.NewDecoder(res.Body).Decode(&out)
	return res.StatusCode, out, res
}
func (e *testEnv) sql(q string, args ...any) {
	e.t.Helper()
	if _, err := e.db.Exec(context.Background(), q, args...); err != nil {
		e.t.Fatal(err)
	}
}
func (e *testEnv) desktop(label string, v2 bool) map[string]any {
	e.t.Helper()
	headers := map[string]string{"X-Kim-Client": "desktop"}
	if v2 {
		headers["X-Kim-Auth-Version"] = "2"
	}
	s, out, res := e.sessionRequest("POST", "kimnotes/auth/desktop/login", credentials{Email: label + "@example.com", Password: "Test-password-123", DeviceName: "Test PC", DeviceID: "test-device"}, nil, "", headers)
	if s != 200 || len(res.Cookies()) != 0 {
		e.t.Fatalf("desktop login: %d %v", s, out)
	}
	return out
}
func (e *testEnv) refresh(pair map[string]any, id string) (int, map[string]any) {
	s, out, _ := e.sessionRequest("POST", "kimnotes/auth/desktop/refresh", map[string]string{"refreshToken": pair["refreshToken"].(string), "requestId": id}, nil, "", nil)
	return s, out
}

func TestWebSessionPolicy(t *testing.T) {
	e := newTestEnv(t)
	c := e.account("webpolicy")
	hash := digest(c.Value)
	if c.MaxAge != 0 || !c.HttpOnly || c.SameSite != http.SameSiteLaxMode {
		t.Fatal("web cookie must end at browser session and be HttpOnly/SameSite")
	}
	s, me, _ := e.request("GET", "kimnotes/auth/me", nil, c)
	if s != 200 {
		t.Fatal(me)
	}
	sid := me["session"].(map[string]any)["id"].(string)
	var idleBefore, idleAfter time.Time
	e.sql("UPDATE sessions SET idle_expires_at=now()+interval '5 minutes',last_activity_at=now()-interval '25 minutes' WHERE id=$1", sid)
	_ = e.db.QueryRow(context.Background(), "SELECT idle_expires_at FROM sessions WHERE id=$1", sid).Scan(&idleBefore)
	if s, _, _ := e.request("GET", "kimnotes/sync/pull?cursor=0", nil, c); s != 200 {
		t.Fatal("background read")
	}
	_ = e.db.QueryRow(context.Background(), "SELECT idle_expires_at FROM sessions WHERE id=$1", sid).Scan(&idleAfter)
	if !idleBefore.Equal(idleAfter) {
		t.Fatal("background sync extended idle timeout")
	}
	e.sql("UPDATE sessions SET last_rotation_at=now()-interval '31 minutes' WHERE id=$1", sid)
	s, activity, res := e.request("POST", "kimnotes/auth/activity", map[string]any{}, c)
	if s != 200 || len(res.Cookies()) != 1 {
		t.Fatal("activity must rotate cookie", s, activity)
	}
	current := res.Cookies()[0]
	if current.Value == c.Value {
		t.Fatal("cookie did not rotate")
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, c); s != 200 {
		t.Fatal("rotation grace must accept in-flight tab")
	}
	s, _, retry := e.request("POST", "kimnotes/auth/activity", map[string]any{}, c)
	if s != 200 || len(retry.Cookies()) != 1 || retry.Cookies()[0].Value != current.Value {
		t.Fatal("lost Set-Cookie response not recoverable")
	}
	e.sql("UPDATE web_cookie_aliases SET expires_at=now()-interval '1 second' WHERE token_hash=$1", hash)
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, c); s != 401 {
		t.Fatal("expired cookie alias accepted")
	}
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, current, "", map[string]string{"X-Kim-Expected-User": newID()}); s != 409 {
		t.Fatal("switched-account write guard absent")
	}
	e.sql("UPDATE sessions SET idle_expires_at=now()-interval '1 second' WHERE id=$1", sid)
	if s, out, _ := e.request("POST", "kimnotes/auth/activity", map[string]any{}, current); s != 401 || out["code"] != "session_expired" {
		t.Fatal("expired idle revived", s, out)
	}
	e.sql("UPDATE sessions SET idle_expires_at=now()+interval '30 minutes',created_at=now()-interval '8 hours 1 minute',absolute_expires_at=now()+interval '1 hour' WHERE id=$1", sid)
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, current); s != 401 {
		t.Fatal("8-hour absolute cap bypassed")
	}
}

func TestMigrationCommandsNeedOnlyDatabase(t *testing.T) {
	t.Setenv("DATABASE_URL", "postgres://tool-only@localhost/test")
	t.Setenv("APP_ENV", "")
	t.Setenv("GATEWAY_SECRET", "")
	t.Setenv("REQUIRE_EMAIL_VERIFICATION", "false")
	for _, mode := range []string{"migrate", "migration-status", "maintenance"} {
		if c, e := loadModeConfig(mode); e != nil || c.DatabaseURL == "" || c.Development {
			t.Fatal("database tool config", mode, e)
		}
	}
	if _, e := loadModeConfig(""); e == nil {
		t.Fatal("normal API must still require production gateway secret")
	}
}

func TestDesktopSessionRotation(t *testing.T) {
	e := newTestEnv(t)
	_ = e.account("rotation")
	pair := e.desktop("rotation", true)
	sid := pair["sessionId"].(string)
	if pair["expiresIn"] != float64(900) || pair["refreshExpiresIn"] != float64(15552000) {
		t.Fatal("lifetimes", pair)
	}
	var device string
	_ = e.db.QueryRow(context.Background(), "SELECT device_id FROM sessions WHERE id=$1", sid).Scan(&device)
	if device != "test-device" {
		t.Fatal("device metadata lost")
	}
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, pair["token"].(string), nil); s != 200 {
		t.Fatal("v2 access rejected")
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, &http.Cookie{Name: "kim_session_kimnotes", Value: pair["token"].(string)}); s != 401 {
		t.Fatal("access accepted as web cookie")
	}
	e.sql("UPDATE access_tokens SET expires_at=now()-interval '1 second' WHERE session_id=$1", sid)
	if s, out, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, pair["token"].(string), nil); s != 401 || out["code"] != "access_expired" {
		t.Fatal("expired access", s, out)
	}
	request := newID()
	var wg sync.WaitGroup
	codes := make([]int, 2)
	results := make([]map[string]any, 2)
	for i := range 2 {
		wg.Add(1)
		go func(i int) { defer wg.Done(); codes[i], results[i] = e.refresh(pair, request) }(i)
	}
	wg.Wait()
	if codes[0] != 200 || codes[1] != 200 || results[0]["refreshToken"] != results[1]["refreshToken"] {
		t.Fatal("concurrent retry must return same pair", codes, results)
	}
	next := results[0]
	if next["refreshToken"] == pair["refreshToken"] {
		t.Fatal("refresh not rotated")
	}
	// A rotation at the old credential deadline still has the entire retry window.
	e.sql("UPDATE refresh_tokens SET expires_at=now()-interval '1 second' WHERE token_hash=$1", digest(pair["refreshToken"].(string)))
	if s, out := e.refresh(pair, request); s != 200 || out["token"] != next["token"] {
		t.Fatal("deadline retry lost", s, out)
	}
	s, out := e.refresh(pair, newID())
	if s != 401 || out["code"] != "refresh_reused" {
		t.Fatal("replay did not revoke family", s, out)
	}
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, next["token"].(string), nil); s != 401 {
		t.Fatal("revoked family's access accepted")
	}
	if s, _ := e.refresh(next, newID()); s != 401 {
		t.Fatal("revoked family's refresh accepted")
	}
}

func TestDesktopUpgradeAndDevices(t *testing.T) {
	e := newTestEnv(t)
	web := e.account("upgrade")
	legacy := e.desktop("upgrade", false)
	req := newID()
	body := map[string]string{"requestId": req, "deviceId": "migration-device"}
	s, pair, _ := e.sessionRequest("POST", "kimnotes/auth/desktop/upgrade", body, nil, legacy["token"].(string), nil)
	if s != 200 {
		t.Fatal("legacy upgrade", s, pair)
	}
	s, retry, _ := e.sessionRequest("POST", "kimnotes/auth/desktop/upgrade", body, nil, legacy["token"].(string), nil)
	if s != 200 || retry["refreshToken"] != pair["refreshToken"] {
		t.Fatal("upgrade retry not idempotent", s, retry)
	}
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, legacy["token"].(string), nil); s != 401 {
		t.Fatal("legacy credential remains valid")
	}
	if s, _, _ := e.sessionRequest("GET", "another-app/auth/me", nil, nil, pair["token"].(string), nil); s != 401 {
		t.Fatal("cross-app bearer accepted")
	}
	if s, _, _ := e.sessionRequest("POST", "another-app/auth/desktop/refresh", map[string]string{"refreshToken": pair["refreshToken"].(string), "requestId": newID()}, nil, "", nil); s != 401 {
		t.Fatal("cross-app refresh accepted")
	}
	s, devices, _ := e.request("GET", "kimnotes/auth/sessions", nil, web)
	if s != 200 || len(devices["sessions"].([]any)) != 2 {
		t.Fatal("device list", s, devices)
	}
	stranger := e.account("stranger")
	if s, _, _ := e.request("DELETE", "kimnotes/auth/sessions/"+pair["sessionId"].(string), nil, stranger); s != 404 {
		t.Fatal("another account revoked device")
	}
	if s, _, _ := e.request("DELETE", "kimnotes/auth/sessions/"+pair["sessionId"].(string), nil, web); s != 200 {
		t.Fatal("revoke device")
	}
	if s, _ := e.refresh(pair, newID()); s != 401 {
		t.Fatal("device revocation did not revoke refresh")
	}
	active := e.desktop("upgrade", true)
	if s, _, _ := e.request("POST", "kimnotes/auth/logout-all", map[string]any{}, web); s != 200 {
		t.Fatal("logout all")
	}
	if s, _ := e.refresh(active, newID()); s != 401 {
		t.Fatal("logout all did not revoke desktop")
	}
	if s, _, _ := e.request("GET", "kimnotes/auth/me", nil, web); s != 401 {
		t.Fatal("logout all did not revoke web")
	}
}

func TestPasswordRevokesPairsAndCacheCleanup(t *testing.T) {
	e := newTestEnv(t)
	web := e.account("passwordpairs")
	pair := e.desktop("passwordpairs", true)
	s, next := e.refresh(pair, newID())
	if s != 200 {
		t.Fatal(s, next)
	}
	e.sql("UPDATE refresh_tokens SET result_expires_at=now()-interval '1 second' WHERE token_hash=$1", digest(pair["refreshToken"].(string)))
	if err := e.app.cleanupSessionCaches(context.Background()); err != nil {
		t.Fatal(err)
	}
	var isNull bool
	_ = e.db.QueryRow(context.Background(), "SELECT result IS NULL FROM refresh_tokens WHERE token_hash=$1", digest(pair["refreshToken"].(string))).Scan(&isNull)
	if !isNull {
		t.Fatal("expired ciphertext retained")
	}
	if s, out, _ := e.request("POST", "kimnotes/auth/password", map[string]string{"currentPassword": "Test-password-123", "newPassword": "New-password-456"}, web); s != 200 {
		t.Fatal("password change", s, out)
	}
	if s, _ := e.refresh(next, newID()); s != 401 {
		t.Fatal("password change left refresh valid")
	}
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, next["token"].(string), nil); s != 401 {
		t.Fatal("password change left access valid")
	}
}

func TestLegacyDeadlineAndDesktopLogout(t *testing.T) {
	e := newTestEnv(t)
	_ = e.account("deadline")
	legacy := e.desktop("deadline", false)
	pair := e.desktop("deadline", true)
	e.sql("UPDATE auth_policy SET legacy_upgrade_until=now()-interval '1 second'")
	if s, _, _ := e.sessionRequest("GET", "kimnotes/auth/me", nil, nil, legacy["token"].(string), nil); s != 401 {
		t.Fatal("legacy cutoff not enforced")
	}
	if s, _, _ := e.sessionRequest("POST", "kimnotes/auth/desktop/login", credentials{Email: "deadline@example.com", Password: "Test-password-123"}, nil, "", map[string]string{"X-Kim-Client": "desktop"}); s != 426 {
		t.Fatal("legacy issuance after cutoff")
	}
	if s, _, _ := e.sessionRequest("POST", "kimnotes/auth/desktop/logout", map[string]string{"refreshToken": pair["refreshToken"].(string)}, nil, "", nil); s != 200 {
		t.Fatal("expired access logout must accept refresh")
	}
	if s, _ := e.refresh(pair, newID()); s != 401 {
		t.Fatal("desktop logout did not revoke family")
	}
	// Secret-bearing response caches are encrypted and AAD-bound.
	sealed, err := e.app.sealSession(map[string]any{"token": strings.Repeat("x", 43)}, "a")
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(sealed, []byte(strings.Repeat("x", 43))) {
		t.Fatal("cached bearer is plaintext")
	}
	if _, err = e.app.openSession(sealed, "b"); err == nil {
		t.Fatal("ciphertext accepted with another session AAD")
	}
}
