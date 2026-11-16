package main

import (
	"context"
	"net/http"
	"strings"
	"sync"
	"testing"
)

var guitarAndroidHeaders = map[string]string{"X-Kim-Client": "desktop", "X-Kim-Auth-Version": "3"}

func guitarAndroidTestEnv(t *testing.T, label string) (*testEnv, *http.Cookie) {
	t.Helper()
	e := newTestEnv(t)
	e.sql("INSERT INTO applications(id,name) VALUES('guitar','吉他学习')")
	e.account(label)
	status, out, response := e.request("POST", "guitar/auth/login", credentials{Email: label + "@example.com", Password: "Test-password-123"}, nil)
	if status != 200 || len(response.Cookies()) == 0 {
		t.Fatalf("guitar web login: %d %v", status, out)
	}
	return e, response.Cookies()[0]
}

func guitarAndroidLogin(t *testing.T, e *testEnv, label, protocol string) map[string]any {
	t.Helper()
	headers := map[string]string{"X-Kim-Client": "desktop", "X-Kim-Auth-Version": protocol}
	status, out, _ := e.sessionRequest("POST", "guitar/auth/desktop/login", credentials{Email: label + "@example.com", Password: "Test-password-123", DeviceName: "小羊吉他 Android", DeviceID: "test-android"}, nil, "", headers)
	if status != 200 {
		t.Fatalf("android login: %d %v", status, out)
	}
	return out
}

func guitarAndroidRefresh(e *testEnv, token, requestID string, headers map[string]string) (int, map[string]any) {
	status, out, _ := e.sessionRequest("POST", "guitar/auth/desktop/refresh", map[string]string{"refreshToken": token, "requestId": requestID}, nil, "", headers)
	return status, out
}

func TestGuitarAndroidPersistentSessionAndRecovery(t *testing.T) {
	e, _ := guitarAndroidTestEnv(t, "android-persistent")
	pair := guitarAndroidLogin(t, e, "android-persistent", "3")
	if pair["protocolVersion"] != float64(3) || pair["refreshExpiresIn"] != nil {
		t.Fatalf("not persistent protocol: %v", pair)
	}
	sid := pair["sessionId"].(string)
	oldToken := pair["refreshToken"].(string)
	e.sql("UPDATE sessions SET expires_at=now()-interval '400 days' WHERE id=$1", sid)
	e.sql("UPDATE refresh_tokens SET expires_at=now()-interval '400 days' WHERE token_hash=$1", digest(oldToken))
	if status, out, _ := e.sessionRequest("GET", "guitar/auth/me", nil, nil, pair["token"].(string), nil); status != 200 {
		t.Fatalf("v3 root idle date invalidated access: %d %v", status, out)
	}
	requestID := newID()
	status, next := guitarAndroidRefresh(e, oldToken, requestID, guitarAndroidHeaders)
	if status != 200 || next["protocolVersion"] != float64(3) {
		t.Fatalf("v3 refresh after inactivity: %d %v", status, next)
	}
	// The server may be offline for arbitrarily longer than the old five-minute
	// retry window; cleanup and gateway-key rotation must not destroy recovery.
	e.sql("UPDATE refresh_tokens SET consumed_at=now()-interval '400 days' WHERE token_hash=$1", digest(oldToken))
	if err := e.app.cleanupSessionCaches(context.Background()); err != nil {
		t.Fatal(err)
	}
	e.app.config.GatewaySecret = "a completely different gateway key"
	status, recovered := guitarAndroidRefresh(e, oldToken, requestID, guitarAndroidHeaders)
	if status != 200 || recovered["refreshToken"] != next["refreshToken"] || recovered["token"] != next["token"] {
		t.Fatalf("lost v3 response not recovered: %d %v", status, recovered)
	}
	status, latest := guitarAndroidRefresh(e, next["refreshToken"].(string), newID(), guitarAndroidHeaders)
	if status != 200 {
		t.Fatalf("successor refresh: %d %v", status, latest)
	}
	var removed bool
	if err := e.db.QueryRow(context.Background(), "SELECT result IS NULL FROM refresh_tokens WHERE token_hash=$1", digest(oldToken)).Scan(&removed); err != nil || !removed {
		t.Fatalf("obsolete recovery result retained: %v", err)
	}
	status, _, _ = e.sessionRequest("POST", "guitar/auth/password", map[string]string{"currentPassword": "Test-password-123", "newPassword": "New-password-456"}, nil, latest["token"].(string), guitarAndroidHeaders)
	if status != 200 {
		t.Fatal("password change failed", status)
	}
	if status, _ = guitarAndroidRefresh(e, latest["refreshToken"].(string), newID(), guitarAndroidHeaders); status != 401 {
		t.Fatal("password change did not revoke persistent device", status)
	}
}

func TestGuitarAndroidMigratesExpiredV2AndLostV2Response(t *testing.T) {
	e, _ := guitarAndroidTestEnv(t, "android-v2")
	pair := guitarAndroidLogin(t, e, "android-v2", "2")
	oldToken := pair["refreshToken"].(string)
	requestID := newID()
	status, lost := guitarAndroidRefresh(e, oldToken, requestID, map[string]string{"X-Kim-Client": "desktop", "X-Kim-Auth-Version": "2"})
	if status != 200 {
		t.Fatal("legacy refresh failed", status, lost)
	}
	e.sql("UPDATE sessions SET expires_at=now()-interval '400 days' WHERE id=$1", pair["sessionId"])
	e.sql("UPDATE refresh_tokens SET expires_at=now()-interval '400 days',result_expires_at=now()-interval '1 second' WHERE token_hash=$1", digest(oldToken))
	if err := e.app.cleanupSessionCaches(context.Background()); err != nil {
		t.Fatal(err)
	}
	status, next := guitarAndroidRefresh(e, oldToken, requestID, guitarAndroidHeaders)
	if status != 200 || next["protocolVersion"] != float64(3) || next["refreshToken"] == lost["refreshToken"] {
		t.Fatalf("legacy pending rotation was not recovered safely: %d %v", status, next)
	}
	status, retry := guitarAndroidRefresh(e, oldToken, requestID, guitarAndroidHeaders)
	if status != 200 || retry["refreshToken"] != next["refreshToken"] {
		t.Fatal("migration response not recoverable", status, retry)
	}
	status, _, _ = e.sessionRequest("POST", "guitar/auth/desktop/logout", map[string]string{"refreshToken": next["refreshToken"].(string)}, nil, "", guitarAndroidHeaders)
	if status != 200 {
		t.Fatal("explicit logout failed", status)
	}
	if status, _ = guitarAndroidRefresh(e, next["refreshToken"].(string), newID(), guitarAndroidHeaders); status != 401 {
		t.Fatal("explicit logout did not revoke device", status)
	}
}

func TestGuitarWebCookieExchangeIsIdempotentAndRequiresValidOriginal(t *testing.T) {
	e, web := guitarAndroidTestEnv(t, "android-cookie")
	requestID := newID()
	body := map[string]string{"requestId": requestID, "deviceId": "test-android"}
	var wg sync.WaitGroup
	results := make([]map[string]any, 4)
	statuses := make([]int, 4)
	for i := range results {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			statuses[i], results[i], _ = e.sessionRequest("POST", "guitar/auth/android/upgrade", body, web, "", guitarAndroidHeaders)
		}(i)
	}
	wg.Wait()
	for i := range results {
		if statuses[i] != 200 || results[i]["protocolVersion"] != float64(3) || results[i]["refreshToken"] != results[0]["refreshToken"] {
			t.Fatalf("concurrent cookie upgrade %d: %d %v", i, statuses[i], results[i])
		}
	}
	e.sql("UPDATE sessions SET idle_expires_at=now()-interval '1 hour' WHERE token_hash=$1", digest(web.Value))
	status, retry, _ := e.sessionRequest("POST", "guitar/auth/android/upgrade", body, web, "", guitarAndroidHeaders)
	if status != 200 || retry["refreshToken"] != results[0]["refreshToken"] {
		t.Fatal("lost cookie upgrade response not recoverable", status, retry)
	}
}

func TestGuitarWebCookieCannotBeUpgradedAfterExpiryWithoutPriorExchange(t *testing.T) {
	e, web := guitarAndroidTestEnv(t, "android-cookie-expired")
	e.sql("UPDATE sessions SET idle_expires_at=now()-interval '1 hour' WHERE token_hash=$1", digest(web.Value))
	status, _, _ := e.sessionRequest("POST", "guitar/auth/android/upgrade", map[string]string{"requestId": newID()}, web, "", guitarAndroidHeaders)
	if status != 401 {
		t.Fatal("already expired web credential was revived", status)
	}
}

func TestGuitarAndroidRegistrationIssuesPersistentSessions(t *testing.T) {
	e := guitarTestEnv(t)
	status, out, _ := e.sessionRequest("POST", "guitar/auth/register", credentials{Email: "android-reg@example.com", Password: "Test-password-123", DeviceName: "小羊吉他 Android", Nickname: "琴友", Avatar: "guitar"}, nil, "", guitarAndroidHeaders)
	if status != 200 || out["protocolVersion"] != float64(3) {
		t.Fatalf("direct registration did not issue v3: %d %v", status, out)
	}
	e.app.config.RequireVerification = true
	e.app.config.SMTPHost, e.app.config.MailFrom = "smtp.test", "notes@example.com"
	var mail string
	e.app.sendMail = func(_ context.Context, _, _, body string) error { mail = body; return nil }
	status, out, _ = e.sessionRequest("POST", "guitar/auth/register", credentials{Email: "android-verify@example.com", Password: "Test-password-123", DeviceName: "小羊吉他 Android", Nickname: "琴友", Avatar: "guitar"}, nil, "", guitarAndroidHeaders)
	if status != 202 {
		t.Fatalf("verification registration: %d %v", status, out)
	}
	code := strings.Split(strings.Split(mail, "：")[1], "\r")[0]
	status, out, _ = e.sessionRequest("POST", "guitar/auth/verify", map[string]string{"challengeId": out["challengeId"].(string), "code": code, "deviceName": "小羊吉他 Android"}, nil, "", guitarAndroidHeaders)
	if status != 200 || out["protocolVersion"] != float64(3) {
		t.Fatalf("verified registration did not issue v3: %d %v", status, out)
	}
}

func TestGuitarAndroidReplayRevokesOnlyCompromisedDevice(t *testing.T) {
	e, _ := guitarAndroidTestEnv(t, "android-replay")
	pair := guitarAndroidLogin(t, e, "android-replay", "3")
	old := pair["refreshToken"].(string)
	status, next := guitarAndroidRefresh(e, old, newID(), guitarAndroidHeaders)
	if status != 200 {
		t.Fatal("first refresh failed", status, next)
	}
	status, out := guitarAndroidRefresh(e, old, newID(), guitarAndroidHeaders)
	if status != 401 || out["code"] != "refresh_reused" {
		t.Fatal("replay was not rejected", status, out)
	}
	if status, _ := guitarAndroidRefresh(e, next["refreshToken"].(string), newID(), guitarAndroidHeaders); status != 401 {
		t.Fatal("compromised device session remained active", status)
	}
	status, out, _ = e.sessionRequest("POST", "kimnotes/auth/desktop/login", credentials{Email: "android-replay@example.com", Password: "Test-password-123"}, nil, "", guitarAndroidHeaders)
	if status != 426 {
		t.Fatal("guitar-only v3 protocol leaked into notes", status, out)
	}
}
