package main

import (
	"errors"
	"net/http"
	"time"

	"github.com/jackc/pgx/v5"
)

// Exchange a still-valid legacy app web cookie without asking for the password.
// The durable request ID makes a lost exchange response recoverable even after
// the original short web session times out. It never revives an expired cookie
// that was not already exchanged, or a session revoked by a password change.
func (a *App) upgradeGuitarWebSession(w http.ResponseWriter, r *http.Request) error {
	if r.PathValue("app") != "guitar" || r.Header.Get("X-Kim-Client") != "desktop" || r.Header.Get("X-Kim-Auth-Version") != "3" {
		return fail(400, "invalid_client", "当前设备不支持此登录方式。")
	}
	var in struct {
		RequestID string `json:"requestId"`
		DeviceID  string `json:"deviceId"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	cookie, err := r.Cookie(a.cookieName("guitar"))
	if err != nil || len(cookie.Value) != 43 || !validID(in.RequestID) || len(in.DeviceID) > 120 {
		return fail(400, "invalid_upgrade", "无法读取原有设备凭证。")
	}
	if err = a.sessionLimit(r); err != nil {
		return err
	}
	hash := digest(cookie.Value)
	if err = a.limited(r, "guitar-web-upgrade:"+hash, 20, time.Minute); err != nil {
		return err
	}
	principal, webErr := a.authenticate(r)
	ctx := r.Context()
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, "SELECT pg_advisory_xact_lock(hashtextextended($1,8437))", hash); err != nil {
		return err
	}
	var sid, user, savedRequest string
	var sealed []byte
	err = tx.QueryRow(ctx, `SELECT x.session_id::text,s.user_id::text,x.request_id::text,x.result
 FROM desktop_upgrades x JOIN sessions s ON s.id=x.session_id
 WHERE x.legacy_hash=$1 AND s.app_id='guitar'`, hash).Scan(&sid, &user, &savedRequest, &sealed)
	if err == nil {
		if savedRequest != in.RequestID {
			return fail(409, "upgrade_in_progress", "原有设备凭证正在恢复，请稍后重试。")
		}
		var currentVersion, sessionVersion int64
		var protocol int
		var revoked *time.Time
		if err = tx.QueryRow(ctx, "SELECT auth_version FROM users WHERE id=$1 FOR SHARE", user).Scan(&currentVersion); err != nil {
			return err
		}
		if err = tx.QueryRow(ctx, "SELECT auth_version,revoked_at,protocol_version FROM sessions WHERE id=$1 FOR UPDATE", sid).Scan(&sessionVersion, &revoked, &protocol); err != nil {
			return err
		}
		if revoked != nil || sessionVersion != currentVersion || protocol != 3 {
			return fail(401, "session_revoked", "设备登录已被撤销，请重新登录。")
		}
		result, err := openGuitarRecovery(sealed, cookie.Value, in.RequestID, sid)
		if err != nil {
			return err
		}
		if err = tx.Commit(ctx); err != nil {
			return err
		}
		writeJSON(w, 200, result)
		return nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return err
	}
	if webErr != nil {
		return webErr
	}
	if principal.ClientKind != "web" || principal.AppID != "guitar" {
		return fail(401, "unauthenticated", "无法确认原有登录。")
	}
	user = principal.UserID
	var email string
	var verified bool
	var currentVersion, webVersion int64
	if err = tx.QueryRow(ctx, "SELECT email,email_verified,auth_version FROM users WHERE id=$1 FOR SHARE", user).Scan(&email, &verified, &currentVersion); err != nil {
		return err
	}
	var revoked, idle, absolute *time.Time
	var expires, created time.Time
	var cookieMatches bool
	err = tx.QueryRow(ctx, `SELECT auth_version,revoked_at,expires_at,idle_expires_at,absolute_expires_at,created_at,
 (token_hash=$2 OR EXISTS(SELECT 1 FROM web_cookie_aliases x WHERE x.token_hash=$2 AND x.session_id=s.id AND x.expires_at>now()))
 FROM sessions s WHERE id=$1 AND app_id='guitar' AND client_kind='web' FOR UPDATE`, principal.SessionID, hash).
		Scan(&webVersion, &revoked, &expires, &idle, &absolute, &created, &cookieMatches)
	if err != nil {
		return err
	}
	now := time.Now().UTC()
	if !cookieMatches || revoked != nil || webVersion != currentVersion || !now.Before(expires) || !now.Before(created.Add(webAbsolute)) || idle == nil || !now.Before(*idle) || absolute == nil || !now.Before(*absolute) {
		return fail(401, "session_revoked", "无法确认原有登录。")
	}
	sid = newID()
	placeholder := time.Date(9999, 12, 31, 0, 0, 0, 0, time.UTC)
	_, err = tx.Exec(ctx, `INSERT INTO sessions(id,app_id,user_id,client_kind,device_name,expires_at,protocol_version,idle_expires_at,auth_version,device_id)
 VALUES($1,'guitar',$2,'desktop','小羊吉他 Android',$3,3,$3,$4,$5)`, sid, user, placeholder, currentVersion, in.DeviceID)
	if err != nil {
		return err
	}
	result, err := a.issuePersistentGuitarPair(ctx, tx, sid, user, email, verified, 0)
	if err != nil {
		return err
	}
	sealed, err = sealGuitarRecovery(result, cookie.Value, in.RequestID, sid)
	if err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, "INSERT INTO desktop_upgrades(legacy_hash,session_id,request_id,result,expires_at) VALUES($1,$2,$3,$4,$5)", hash, sid, in.RequestID, sealed, placeholder); err != nil {
		return err
	}
	if err = authEvent(ctx, tx, user, "guitar", sid, "login"); err != nil {
		return err
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	writeJSON(w, 200, result)
	return nil
}
