package main

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"github.com/jackc/pgx/v5"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"time"
)

const webIdle = 30 * time.Minute
const webAbsolute = 8 * time.Hour
const desktopAccess = 15 * time.Minute
const desktopIdle = 180 * 24 * time.Hour

func authEvent(ctx context.Context, tx pgx.Tx, user, app, sid, event string) error {
	_, e := tx.Exec(ctx, "INSERT INTO auth_events(user_id,app_id,session_id,event) VALUES($1,$2,$3,$4)", user, app, sid, event)
	return e
}

// All security mutations lock the user before the session, matching refresh and
// password changes. This also keeps the audit FK from reversing lock order.
func (a *App) revokeDevice(ctx context.Context, user, sid, event string) (bool, error) {
	tx, e := a.db.Begin(ctx)
	if e != nil {
		return false, e
	}
	defer tx.Rollback(ctx)
	var id, app string
	if e = tx.QueryRow(ctx, "SELECT id::text FROM users WHERE id=$1 FOR SHARE", user).Scan(&id); e != nil {
		if e == pgx.ErrNoRows {
			return false, nil
		}
		return false, e
	}
	if e = tx.QueryRow(ctx, "UPDATE sessions SET revoked_at=COALESCE(revoked_at,now()) WHERE id=$1 AND user_id=$2 RETURNING app_id", sid, user).Scan(&app); e != nil {
		if e == pgx.ErrNoRows {
			return false, nil
		}
		return false, e
	}
	if e = authEvent(ctx, tx, user, app, sid, event); e != nil {
		return false, e
	}
	e = tx.Commit(ctx)
	return e == nil, e
}

// Clear encrypted retry responses even when a device never returns. Consumed
// hashes remain available for replay detection throughout the session lifetime.
func (a *App) cleanupSessionCaches(ctx context.Context) error {
	for _, q := range []string{
		"UPDATE refresh_tokens SET result=NULL WHERE result_expires_at<now() AND result IS NOT NULL",
		"DELETE FROM desktop_upgrades WHERE expires_at<now()",
		"DELETE FROM web_cookie_aliases WHERE expires_at<now()",
		"DELETE FROM auth_challenges WHERE expires_at<=now()",
		"DELETE FROM note_pickup_codes WHERE expires_at<=now() AND (claimed_at IS NULL OR claimed_at<=now()-interval '1 minute')",
		"DELETE FROM access_tokens WHERE expires_at<now()-interval '1 day'",
		"DELETE FROM rate_limits WHERE window_start<now()-interval '1 day'",
		"DELETE FROM auth_events WHERE created_at<now()-interval '90 days'",
	} {
		if _, e := a.db.Exec(ctx, q); e != nil {
			return e
		}
	}
	return nil
}

func (a *App) maintainSessionCaches(ctx context.Context) {
	timer := time.NewTicker(5 * time.Minute)
	defer timer.Stop()
	for {
		work, cancel := context.WithTimeout(ctx, 15*time.Second)
		if e := a.cleanupSessionCaches(work); e != nil && ctx.Err() == nil {
			slog.Error("session cache cleanup failed", "error", e)
		}
		cancel()
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
		}
	}
}

func (a *App) createManagedSession(w http.ResponseWriter, r *http.Request, id, email string, verified bool, device, deviceID, expectedHash string) error {
	kind := "web"
	if r.Header.Get("X-Kim-Client") == "desktop" {
		kind = "desktop"
	}
	v2 := kind == "desktop" && r.Header.Get("X-Kim-Auth-Version") == "2"
	v3 := kind == "desktop" && r.Header.Get("X-Kim-Auth-Version") == "3" && r.PathValue("app") == "guitar"
	if kind == "desktop" && r.Header.Get("X-Kim-Auth-Version") == "3" && !v3 {
		return fail(426, "client_upgrade_required", "当前应用不支持此设备登录方式。")
	}
	if kind == "desktop" && !v2 && !v3 {
		return fail(426, "client_upgrade_required", "请更新桌面应用后登录。")
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var current string
	var version int64
	if e = tx.QueryRow(r.Context(), "SELECT password_hash,auth_version FROM users WHERE id=$1 FOR SHARE", id).Scan(&current, &version); e != nil {
		return e
	}
	if !secureEqual(current, expectedHash) {
		return fail(401, "credentials_changed", "密码已变更，请重新登录。")
	}
	var token, tokenHash string
	if kind == "web" {
		token = randomToken()
		tokenHash = digest(token)
	}
	sid := newID()
	now := time.Now().UTC()
	expires := now.Add(webAbsolute)
	idle := now.Add(webIdle)
	protocol := 2
	if kind == "desktop" {
		expires = now.Add(desktopIdle)
		idle = expires
	}
	if v3 {
		protocol = 3
	}
	device = string([]rune(device)[:min(len([]rune(device)), 120)])
	if device == "" {
		if kind == "web" {
			device = "网页浏览器"
		} else {
			device = "Windows 便签"
		}
	}
	if len(deviceID) > 120 {
		return fail(400, "invalid_device", "设备标识过长。")
	}
	_, e = tx.Exec(r.Context(), `INSERT INTO sessions(token_hash,id,app_id,user_id,client_kind,device_name,expires_at,protocol_version,idle_expires_at,absolute_expires_at,auth_version,device_id)
 VALUES(NULLIF($1,''),$2,$3,$4,$5,$6,$7,$8,$9,CASE WHEN $5='web' THEN $7::timestamptz ELSE NULL END,$10,$11)`, tokenHash, sid, r.PathValue("app"), id, kind, device, expires, protocol, idle, version, deviceID)
	if e != nil {
		return e
	}
	var out map[string]any
	if v3 {
		out, e = a.issuePersistentGuitarPair(r.Context(), tx, sid, id, email, verified, 0)
		if e != nil {
			return e
		}
	} else if v2 {
		out, e = a.issuePair(r.Context(), tx, sid, r.PathValue("app"), id, email, verified, 0)
		if e != nil {
			return e
		}
	} else {
		user, err := noteUser(r.Context(), tx, r.PathValue("app"), id, email, verified)
		if err != nil {
			return err
		}
		out = map[string]any{"user": user, "appId": r.PathValue("app"), "expiresIn": int(webAbsolute.Seconds()), "sessionId": sid, "protocolVersion": protocol}
	}
	if e = authEvent(r.Context(), tx, id, r.PathValue("app"), sid, "login"); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	if kind == "web" {
		a.cookie(w, r.PathValue("app"), token, 0)
	}
	writeJSON(w, 200, out)
	return nil
}

func (a *App) authenticate(r *http.Request) (principal, error) {
	p := principal{AppID: r.PathValue("app"), ClientKind: "web"}
	token := ""
	if h := r.Header.Get("Authorization"); strings.HasPrefix(h, "Bearer ") {
		token = strings.TrimPrefix(h, "Bearer ")
		p.ClientKind = "desktop"
	} else if c, e := r.Cookie(a.cookieName(p.AppID)); e == nil {
		token = c.Value
	}
	if len(token) != 43 {
		return p, fail(401, "unauthenticated", "请先登录。")
	}
	p.TokenHash = digest(token)
	var expires, created time.Time
	var revoked, accessExpiry *time.Time
	var version, currentVersion int64
	query := `SELECT s.user_id::text,u.email,u.email_verified,s.id::text,s.protocol_version,s.idle_expires_at,s.absolute_expires_at,s.expires_at,s.created_at,s.revoked_at,s.auth_version,u.auth_version,NULL::timestamptz,s.device_name
 FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.app_id=$2 AND s.client_kind='web' AND
 (s.token_hash=$1 OR EXISTS(SELECT 1 FROM web_cookie_aliases x WHERE x.token_hash=$1 AND x.session_id=s.id AND x.expires_at>now()))`
	if p.ClientKind == "desktop" {
		query = `SELECT s.user_id::text,u.email,u.email_verified,s.id::text,s.protocol_version,s.idle_expires_at,s.absolute_expires_at,s.expires_at,s.created_at,s.revoked_at,s.auth_version,u.auth_version,t.expires_at,s.device_name
 FROM access_tokens t JOIN sessions s ON s.id=t.session_id JOIN users u ON u.id=s.user_id
 WHERE s.app_id=$2 AND s.client_kind='desktop' AND s.protocol_version IN (2,3) AND t.token_hash=$1`
	}
	e := a.db.QueryRow(r.Context(), query, p.TokenHash, p.AppID).Scan(&p.UserID, &p.Email, &p.Verified, &p.SessionID, &p.Protocol, &p.IdleExpires, &p.AbsoluteExpires, &expires, &created, &revoked, &version, &currentVersion, &accessExpiry, &p.DeviceName)
	if errors.Is(e, pgx.ErrNoRows) {
		return p, fail(401, "unauthenticated", "登录已失效，请重新验证。")
	}
	if e != nil {
		return p, e
	}
	if revoked != nil || version != currentVersion {
		return p, fail(401, "session_revoked", "此设备的登录已撤销，请重新登录。")
	}
	now := time.Now().UTC()
	if p.ClientKind == "web" {
		if !now.Before(expires) || !now.Before(created.Add(webAbsolute)) || p.IdleExpires == nil || !now.Before(*p.IdleExpires) || p.AbsoluteExpires == nil || !now.Before(*p.AbsoluteExpires) {
			return p, fail(401, "session_expired", "登录已过期，未保存的内容将为你保留，请重新登录。")
		}
	} else {
		if p.Protocol != 3 && !now.Before(expires) {
			return p, fail(401, "refresh_expired", "此设备长期未使用，请重新登录。")
		}
		if accessExpiry == nil || !now.Before(*accessExpiry) {
			return p, fail(401, "access_expired", "访问凭证已到期，请续期。")
		}
		_, e = a.db.Exec(r.Context(), "UPDATE sessions SET last_activity_at=now() WHERE id=$1 AND last_activity_at<now()-interval '1 minute'", p.SessionID)
		if e != nil {
			return p, e
		}
	}
	return p, nil
}

func (a *App) issuePair(ctx context.Context, tx pgx.Tx, sid, app, user, email string, verified bool, generation int64) (map[string]any, error) {
	profile, err := noteUser(ctx, tx, app, user, email, verified)
	if err != nil {
		return nil, err
	}
	now := time.Now().UTC()
	access, refresh := randomToken(), randomToken()
	expiry := now.Add(desktopIdle)
	if _, e := tx.Exec(ctx, "INSERT INTO access_tokens(token_hash,session_id,expires_at) VALUES($1,$2,$3)", digest(access), sid, now.Add(desktopAccess)); e != nil {
		return nil, e
	}
	if _, e := tx.Exec(ctx, "INSERT INTO refresh_tokens(token_hash,session_id,generation,expires_at) VALUES($1,$2,$3,$4)", digest(refresh), sid, generation, expiry); e != nil {
		return nil, e
	}
	if _, e := tx.Exec(ctx, "UPDATE sessions SET expires_at=$2,idle_expires_at=$2,last_activity_at=now() WHERE id=$1", sid, expiry); e != nil {
		return nil, e
	}
	if _, e := tx.Exec(ctx, "DELETE FROM access_tokens WHERE session_id=$1 AND expires_at<now()-interval '1 day'", sid); e != nil {
		return nil, e
	}
	return map[string]any{"user": profile, "appId": app, "token": access, "refreshToken": refresh, "expiresIn": 900, "refreshExpiresIn": 15552000, "sessionId": sid, "protocolVersion": 2, "accessExpiresAt": now.Add(desktopAccess), "refreshExpiresAt": expiry}, nil
}

// Protocol 3 is restricted to the guitar Android app. The far-future value
// satisfies the existing NOT NULL columns; authorization does not expire v3
// sessions based on this timestamp. Password changes and revocation still win.
func (a *App) issuePersistentGuitarPair(ctx context.Context, tx pgx.Tx, sid, user, email string, verified bool, generation int64) (map[string]any, error) {
	now := time.Now().UTC()
	access, refresh := randomToken(), randomToken()
	placeholder := time.Date(9999, 12, 31, 0, 0, 0, 0, time.UTC)
	if _, err := tx.Exec(ctx, "INSERT INTO access_tokens(token_hash,session_id,expires_at) VALUES($1,$2,$3)", digest(access), sid, now.Add(desktopAccess)); err != nil {
		return nil, err
	}
	if _, err := tx.Exec(ctx, "INSERT INTO refresh_tokens(token_hash,session_id,generation,expires_at) VALUES($1,$2,$3,$4)", digest(refresh), sid, generation, placeholder); err != nil {
		return nil, err
	}
	if _, err := tx.Exec(ctx, "UPDATE sessions SET protocol_version=3,expires_at=$2,idle_expires_at=$2,last_activity_at=now() WHERE id=$1 AND app_id='guitar'", sid, placeholder); err != nil {
		return nil, err
	}
	if _, err := tx.Exec(ctx, "DELETE FROM access_tokens WHERE session_id=$1 AND expires_at<now()-interval '1 day'", sid); err != nil {
		return nil, err
	}
	return map[string]any{"user": map[string]any{"id": user, "email": email, "emailVerified": verified}, "appId": "guitar", "token": access, "refreshToken": refresh, "expiresIn": 900, "sessionId": sid, "protocolVersion": 3, "accessExpiresAt": now.Add(desktopAccess), "refreshExpiresIn": nil}, nil
}

// The retry result is encrypted with the old high-entropy token plus the
// request id, rather than a server secret which can be rotated independently.
// Only the immediately preceding unacknowledged result remains recoverable.
func guitarRecoveryAEAD(refresh, requestID string) (cipher.AEAD, error) {
	key := sha256.Sum256([]byte("guitar-android-recovery-v3:" + refresh + ":" + requestID))
	block, err := aes.NewCipher(key[:])
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}

func sealGuitarRecovery(value map[string]any, refresh, requestID, sid string) ([]byte, error) {
	g, err := guitarRecoveryAEAD(refresh, requestID)
	if err != nil {
		return nil, err
	}
	plain, err := json.Marshal(value)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, g.NonceSize())
	if _, err = io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	return g.Seal(nonce, nonce, plain, []byte("guitar:"+sid+":"+digest(refresh)+":"+requestID)), nil
}

func openGuitarRecovery(raw []byte, refresh, requestID, sid string) (map[string]any, error) {
	g, err := guitarRecoveryAEAD(refresh, requestID)
	if err != nil {
		return nil, err
	}
	if len(raw) < g.NonceSize() {
		return nil, fail(401, "refresh_revoked", "设备凭证已撤销，请重新登录。")
	}
	plain, err := g.Open(nil, raw[:g.NonceSize()], raw[g.NonceSize():], []byte("guitar:"+sid+":"+digest(refresh)+":"+requestID))
	if err != nil {
		return nil, fail(401, "refresh_revoked", "设备凭证已撤销，请重新登录。")
	}
	var result map[string]any
	if err = json.Unmarshal(plain, &result); err != nil {
		return nil, err
	}
	return result, nil
}

// A persisted request ID can recover a lost rotation for as long as the user
// remains signed in. Once the successor is used, the older encrypted result
// is discarded. Existing v2 Android sessions can migrate even after idle
// expiry, provided their account/session has not been revoked.
func (a *App) refreshPersistentGuitar(w http.ResponseWriter, r *http.Request, tx pgx.Tx, sid, user, email, refresh, requestID string, verified bool, protocol int, generation int64, consumed *time.Time, previousRequest *string, sealed []byte) error {
	ctx := r.Context()
	hash := digest(refresh)
	if consumed != nil && (previousRequest == nil || *previousRequest != requestID) {
		if _, err := tx.Exec(ctx, "UPDATE sessions SET revoked_at=now() WHERE id=$1", sid); err != nil {
			return err
		}
		if err := authEvent(ctx, tx, user, "guitar", sid, "refresh_reused"); err != nil {
			return err
		}
		if err := tx.Commit(ctx); err != nil {
			return err
		}
		return fail(401, "refresh_reused", "设备登录已由服务端撤销，请重新登录。")
	}
	if protocol == 3 && consumed != nil {
		result, err := openGuitarRecovery(sealed, refresh, requestID, sid)
		if err != nil {
			return err
		}
		if err = tx.Commit(ctx); err != nil {
			return err
		}
		writeJSON(w, 200, result)
		return nil
	}
	if protocol == 2 && consumed != nil {
		// A legacy v2 response may have been lost after its five-minute cache
		// was purged. The matching durable request id proves this is that retry;
		// invalidate any inaccessible successor before creating a v3 pair.
		if _, err := tx.Exec(ctx, "UPDATE refresh_tokens SET consumed_at=COALESCE(consumed_at,now()),result=NULL,result_expires_at=NULL WHERE session_id=$1 AND token_hash<>$2", sid, hash); err != nil {
			return err
		}
	} else if _, err := tx.Exec(ctx, "UPDATE refresh_tokens SET result=NULL,result_expires_at=NULL WHERE session_id=$1 AND token_hash<>$2 AND result IS NOT NULL", sid, hash); err != nil {
		return err
	}
	// A web-to-Android exchange no longer needs its recovery result after the
	// newly issued device refresh token is first used.
	if _, err := tx.Exec(ctx, "DELETE FROM desktop_upgrades WHERE session_id=$1", sid); err != nil {
		return err
	}
	out, err := a.issuePersistentGuitarPair(ctx, tx, sid, user, email, verified, generation+1)
	if err != nil {
		return err
	}
	result, err := sealGuitarRecovery(out, refresh, requestID, sid)
	if err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, "UPDATE refresh_tokens SET consumed_at=now(),request_id=$2,result=$3,result_expires_at=NULL WHERE token_hash=$1", hash, requestID, result); err != nil {
		return err
	}
	// Keep a bounded replay-detection history in addition to the one recovery
	// result, so a persistent device does not grow the database without bound.
	if _, err = tx.Exec(ctx, `DELETE FROM refresh_tokens WHERE session_id=$1 AND consumed_at IS NOT NULL AND token_hash<>$2
 AND token_hash NOT IN (SELECT token_hash FROM refresh_tokens WHERE session_id=$1 AND consumed_at IS NOT NULL AND token_hash<>$2 ORDER BY generation DESC,created_at DESC LIMIT 16)`, sid, hash); err != nil {
		return err
	}
	if err = authEvent(ctx, tx, user, "guitar", sid, "refresh"); err != nil {
		return err
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	writeJSON(w, 200, out)
	return nil
}

func (a *App) sealSession(value map[string]any, aad string) ([]byte, error) {
	key := sha256.Sum256([]byte("kimnotes-refresh-cache-v2:" + a.config.GatewaySecret))
	block, e := aes.NewCipher(key[:])
	if e != nil {
		return nil, e
	}
	g, e := cipher.NewGCM(block)
	if e != nil {
		return nil, e
	}
	raw, e := json.Marshal(value)
	if e != nil {
		return nil, e
	}
	nonce := make([]byte, g.NonceSize())
	if _, e = io.ReadFull(rand.Reader, nonce); e != nil {
		return nil, e
	}
	return g.Seal(nonce, nonce, raw, []byte(aad)), nil
}
func (a *App) openSession(raw []byte, aad string) (map[string]any, error) {
	key := sha256.Sum256([]byte("kimnotes-refresh-cache-v2:" + a.config.GatewaySecret))
	block, e := aes.NewCipher(key[:])
	if e != nil {
		return nil, e
	}
	g, e := cipher.NewGCM(block)
	if e != nil {
		return nil, e
	}
	if len(raw) < g.NonceSize() {
		return nil, fail(401, "refresh_revoked", "续期结果不可恢复，请重新登录。")
	}
	plain, e := g.Open(nil, raw[:g.NonceSize()], raw[g.NonceSize():], []byte(aad))
	if e != nil {
		return nil, fail(401, "refresh_revoked", "续期结果不可恢复，请重新登录。")
	}
	var result map[string]any
	e = json.Unmarshal(plain, &result)
	return result, e
}

func (a *App) refreshDesktop(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Refresh   string `json:"refreshToken"`
		RequestID string `json:"requestId"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if len(in.Refresh) != 43 || !validID(in.RequestID) {
		return fail(400, "invalid_refresh", "续期请求无效。")
	}
	if e := a.sessionLimit(r); e != nil {
		return e
	}
	hash := digest(in.Refresh)
	if e := a.limited(r, "refresh:"+hash, 30, time.Minute); e != nil {
		return e
	}
	var user, sid string
	if e := a.db.QueryRow(r.Context(), "SELECT s.user_id::text,s.id::text FROM refresh_tokens t JOIN sessions s ON s.id=t.session_id WHERE t.token_hash=$1 AND s.app_id=$2 AND s.client_kind='desktop'", hash, r.PathValue("app")).Scan(&user, &sid); e != nil {
		if e == pgx.ErrNoRows {
			return fail(401, "refresh_revoked", "设备凭证已失效，请重新登录。")
		}
		return e
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var currentVersion, version, generation int64
	var email, deviceName string
	var sessionProtocol int
	var verified bool
	if e = tx.QueryRow(r.Context(), "SELECT auth_version,email,email_verified FROM users WHERE id=$1 FOR SHARE", user).Scan(&currentVersion, &email, &verified); e != nil {
		return e
	}
	var revoked, consumed, cachedUntil *time.Time
	var expires, rootExpiry time.Time
	var previousRequest *string
	var sealed []byte
	if e = tx.QueryRow(r.Context(), "SELECT auth_version,revoked_at,expires_at,protocol_version,device_name FROM sessions WHERE id=$1 FOR UPDATE", sid).Scan(&version, &revoked, &rootExpiry, &sessionProtocol, &deviceName); e != nil {
		return fail(401, "refresh_revoked", "设备凭证已失效，请重新登录。")
	}
	if e = tx.QueryRow(r.Context(), "SELECT generation,expires_at,consumed_at,request_id::text,result,result_expires_at FROM refresh_tokens WHERE token_hash=$1 FOR UPDATE", hash).Scan(&generation, &expires, &consumed, &previousRequest, &sealed, &cachedUntil); e != nil {
		return e
	}
	now := time.Now().UTC()
	if revoked != nil || version != currentVersion {
		return fail(401, "refresh_revoked", "此设备登录已撤销，请重新登录。")
	}
	wantGuitarPersistent := r.PathValue("app") == "guitar" && r.Header.Get("X-Kim-Client") == "desktop" && r.Header.Get("X-Kim-Auth-Version") == "3"
	if sessionProtocol == 3 || wantGuitarPersistent {
		if !wantGuitarPersistent || (sessionProtocol != 3 && (sessionProtocol != 2 || deviceName != "小羊吉他 Android")) {
			return fail(426, "client_upgrade_required", "当前设备登录方式不支持此操作。")
		}
		return a.refreshPersistentGuitar(w, r, tx, sid, user, email, in.Refresh, in.RequestID, verified, sessionProtocol, generation, consumed, previousRequest, sealed)
	}
	if !now.Before(rootExpiry) {
		return fail(401, "refresh_expired", "此设备长期未使用，请重新登录。")
	}
	aad := "refresh:" + r.PathValue("app") + ":" + hash + ":" + in.RequestID
	if consumed != nil {
		if previousRequest != nil && *previousRequest == in.RequestID {
			if cachedUntil == nil || !now.Before(*cachedUntil) {
				return fail(401, "refresh_expired", "续期恢复窗口已结束，请重新登录。")
			}
			result, e := a.openSession(sealed, aad)
			if e != nil {
				return e
			}
			if e = tx.Commit(r.Context()); e != nil {
				return e
			}
			writeJSON(w, 200, result)
			return nil
		}
		if _, e = tx.Exec(r.Context(), "UPDATE sessions SET revoked_at=now() WHERE id=$1", sid); e != nil {
			return e
		}
		if e = authEvent(r.Context(), tx, user, r.PathValue("app"), sid, "refresh_reused"); e != nil {
			return e
		}
		if e = tx.Commit(r.Context()); e != nil {
			return e
		}
		return fail(401, "refresh_reused", "检测到设备凭证重复使用，此设备已退出，请重新登录。")
	}
	if !now.Before(expires) {
		return fail(401, "refresh_expired", "此设备长期未使用，请重新登录。")
	}
	out, e := a.issuePair(r.Context(), tx, sid, r.PathValue("app"), user, email, verified, generation+1)
	if e != nil {
		return e
	}
	encrypted, e := a.sealSession(out, aad)
	if e != nil {
		return e
	}
	_, e = tx.Exec(r.Context(), "UPDATE refresh_tokens SET consumed_at=now(),request_id=$2,result=$3,result_expires_at=now()+interval '5 minutes' WHERE token_hash=$1", hash, in.RequestID, encrypted)
	if e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "UPDATE refresh_tokens SET result=NULL WHERE session_id=$1 AND result_expires_at<now() AND result IS NOT NULL", sid); e != nil {
		return e
	}
	if e = authEvent(r.Context(), tx, user, r.PathValue("app"), sid, "refresh"); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 200, out)
	return nil
}

func (a *App) upgradeDesktop(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		RequestID  string `json:"requestId"`
		DeviceName string `json:"deviceName,omitempty"`
		DeviceID   string `json:"deviceId,omitempty"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if len(token) != 43 || !validID(in.RequestID) {
		return fail(401, "unauthenticated", "请重新登录。")
	}
	if len(in.DeviceID) > 120 {
		return fail(400, "invalid_device", "设备标识过长。")
	}
	if e := a.sessionLimit(r); e != nil {
		return e
	}
	if e := a.limited(r, "upgrade:"+digest(token), 20, time.Minute); e != nil {
		return e
	}
	return fail(401, "session_revoked", "旧凭证升级窗口已结束，请重新登录。")
}

func (a *App) logoutDesktop(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Refresh string `json:"refreshToken"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if len(in.Refresh) != 43 {
		return fail(401, "refresh_revoked", "设备凭证无效。")
	}
	if e := a.sessionLimit(r); e != nil {
		return e
	}
	var user, sid string
	e := a.db.QueryRow(r.Context(), "SELECT s.user_id::text,s.id::text FROM sessions s JOIN refresh_tokens t ON t.session_id=s.id WHERE s.app_id=$2 AND s.client_kind='desktop' AND t.token_hash=$1", digest(in.Refresh), r.PathValue("app")).Scan(&user, &sid)
	if e != nil && e != pgx.ErrNoRows {
		return e
	}
	if e == nil {
		if _, e = a.revokeDevice(r.Context(), user, sid, "logout"); e != nil {
			return e
		}
	}
	writeJSON(w, 200, map[string]bool{"ok": true})
	return nil
}

func (a *App) activity(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.ClientKind != "web" {
		return fail(403, "web_session_required", "此接口仅用于网页会话。")
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var hash string
	var rotation, absolute, idle time.Time
	e = tx.QueryRow(r.Context(), "SELECT token_hash,last_rotation_at,absolute_expires_at,idle_expires_at FROM sessions WHERE id=$1 AND revoked_at IS NULL AND idle_expires_at>now() AND absolute_expires_at>now() FOR UPDATE", p.SessionID).Scan(&hash, &rotation, &absolute, &idle)
	if e == pgx.ErrNoRows {
		return fail(401, "session_expired", "登录已过期，请重新登录。")
	}
	if e != nil {
		return e
	}
	now := time.Now().UTC()
	next := now.Add(webIdle)
	if next.After(absolute) {
		next = absolute
	}
	token := ""
	if now.Sub(rotation) >= 30*time.Minute {
		token = randomToken()
		sealed, e := a.sealSession(map[string]any{"token": token}, "web:"+p.SessionID+":"+hash)
		if e != nil {
			return e
		}
		if _, e = tx.Exec(r.Context(), "INSERT INTO web_cookie_aliases(token_hash,session_id,expires_at,result) VALUES($1,$2,now()+interval '1 minute',$3) ON CONFLICT(token_hash) DO NOTHING", hash, p.SessionID, sealed); e != nil {
			return e
		}
		if _, e = tx.Exec(r.Context(), "UPDATE sessions SET token_hash=$2,last_rotation_at=now() WHERE id=$1", p.SessionID, digest(token)); e != nil {
			return e
		}
	} else if p.TokenHash != hash {
		// Recover a lost Set-Cookie response, including simultaneous browser tabs,
		// without repeatedly rotating or resetting the absolute session deadline.
		var sealed []byte
		if e = tx.QueryRow(r.Context(), "SELECT result FROM web_cookie_aliases WHERE token_hash=$1 AND session_id=$2 AND expires_at>now()", p.TokenHash, p.SessionID).Scan(&sealed); e != nil {
			return fail(401, "session_expired", "登录状态已更新，请重新登录。")
		}
		out, e := a.openSession(sealed, "web:"+p.SessionID+":"+p.TokenHash)
		if e != nil {
			return e
		}
		token, _ = out["token"].(string)
	}
	if _, e = tx.Exec(r.Context(), "UPDATE sessions SET last_activity_at=now(),idle_expires_at=$2 WHERE id=$1", p.SessionID, next); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "DELETE FROM web_cookie_aliases WHERE session_id=$1 AND expires_at<now()", p.SessionID); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	if token != "" {
		a.cookie(w, p.AppID, token, 0)
	}
	writeJSON(w, 200, map[string]any{"idleExpiresAt": next, "absoluteExpiresAt": absolute})
	return nil
}

func (a *App) listSessions(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	rows, e := a.db.Query(r.Context(), `SELECT s.id::text,s.app_id,s.client_kind,s.device_name,s.created_at,s.last_activity_at,s.expires_at FROM sessions s JOIN users u ON u.id=s.user_id
 WHERE s.user_id=$1 AND s.revoked_at IS NULL AND s.expires_at>now() AND s.auth_version=u.auth_version AND (s.client_kind='desktop' OR s.idle_expires_at>now()) ORDER BY s.last_activity_at DESC LIMIT 100`, p.UserID)
	if e != nil {
		return e
	}
	defer rows.Close()
	values := make([]map[string]any, 0)
	for rows.Next() {
		var id, app, kind, device string
		var created, active, expires time.Time
		if e = rows.Scan(&id, &app, &kind, &device, &created, &active, &expires); e != nil {
			return e
		}
		values = append(values, map[string]any{"id": id, "appId": app, "clientKind": kind, "deviceName": device, "createdAt": created, "lastActiveAt": active, "expiresAt": expires, "current": id == p.SessionID})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"sessions": values})
	return nil
}
func (a *App) revokeSession(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	id := r.PathValue("session")
	if !validID(id) {
		return fail(404, "not_found", "设备不存在。")
	}
	found, e := a.revokeDevice(r.Context(), p.UserID, id, "device_revoked")
	if e != nil {
		return e
	}
	if !found {
		return fail(404, "not_found", "设备不存在。")
	}
	current := id == p.SessionID
	if current && p.ClientKind == "web" {
		a.cookie(w, p.AppID, "", -1)
	}
	writeJSON(w, 200, map[string]bool{"ok": true, "current": current})
	return nil
}
func (a *App) logoutAll(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	if _, e = tx.Exec(r.Context(), "UPDATE users SET auth_version=auth_version+1 WHERE id=$1", p.UserID); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "UPDATE sessions SET revoked_at=now() WHERE user_id=$1", p.UserID); e != nil {
		return e
	}
	if e = authEvent(r.Context(), tx, p.UserID, p.AppID, p.SessionID, "logout_all"); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	a.cookie(w, p.AppID, "", -1)
	writeJSON(w, 200, map[string]bool{"ok": true})
	return nil
}
