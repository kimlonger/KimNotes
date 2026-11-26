package main

import (
	"crypto/sha256"
	"encoding/base64"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

const browserHandoffSeconds = 90

func decodeHandoffToken(value string) []byte {
	if len(value) != 43 {
		return nil
	}
	raw, err := base64.RawURLEncoding.Strict().DecodeString(value)
	if err != nil || len(raw) != sha256.Size {
		return nil
	}
	return raw
}

func validHandoffVerifier(s string) bool {
	if len(s) < 43 || len(s) > 128 {
		return false
	}
	for _, c := range s {
		if !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || strings.ContainsRune("-._~", c)) {
			return false
		}
	}
	return true
}

func invalidHandoff() error {
	return fail(http.StatusUnauthorized, "invalid_handoff", "登录请求已失效，请从桌面便签重新打开。")
}

func (a *App) createBrowserHandoff(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.AppID != "kimnotes" {
		return fail(http.StatusNotFound, "not_found", "入口不存在。")
	}
	if p.ClientKind != "desktop" || p.Protocol != 2 || r.Header.Get("X-Kim-Client") != "desktop" {
		return fail(http.StatusForbidden, "desktop_required", "请从桌面便签打开。")
	}
	var in struct{ Challenge string }
	if err := decode(w, r, &in); err != nil {
		return err
	}
	challenge := decodeHandoffToken(in.Challenge)
	if challenge == nil {
		return fail(http.StatusBadRequest, "invalid_challenge", "登录请求无效。")
	}
	// Bound both outstanding codes and short-lived storage per authenticated device.
	if err := a.limited(r, "notes:browser-handoff:"+p.SessionID, 8, time.Minute); err != nil {
		return err
	}
	code := randomToken()
	tag, err := a.db.Exec(r.Context(), `INSERT INTO note_browser_handoffs(code_hash, challenge, source_user_id, source_session_id, expires_at)
		SELECT $1,$2,u.id,s.id,clock_timestamp()+$5*interval '1 second'
		FROM sessions s JOIN users u ON u.id=s.user_id
		WHERE s.id=$3 AND u.id=$4 AND s.app_id='kimnotes' AND s.client_kind='desktop' AND s.protocol_version=2
		  AND s.revoked_at IS NULL AND s.auth_version=u.auth_version AND s.expires_at>clock_timestamp()`,
		digest(code), challenge, p.SessionID, p.UserID, browserHandoffSeconds)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return invalidHandoff()
	}
	writeJSON(w, http.StatusOK, map[string]any{"code": code, "expiresIn": browserHandoffSeconds})
	return nil
}

func (a *App) consumeBrowserHandoff(w http.ResponseWriter, r *http.Request) error {
	if r.PathValue("app") != "kimnotes" {
		return fail(http.StatusNotFound, "not_found", "入口不存在。")
	}
	client := r.Header.Get("X-Kim-Client")
	fetchSite := r.Header.Get("Sec-Fetch-Site")
	if r.Header.Get("Origin") != a.config.PublicOrigin || (fetchSite != "" && fetchSite != "same-origin") ||
		(client != "" && client != "web") || r.Header.Get("Authorization") != "" {
		return fail(http.StatusForbidden, "browser_required", "请通过便签官网登录。")
	}
	var in struct{ Code, Verifier string }
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if decodeHandoffToken(in.Code) == nil || !validHandoffVerifier(in.Verifier) {
		return invalidHandoff()
	}
	if err := a.sessionLimit(r); err != nil {
		return err
	}
	ctx := r.Context()
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	codeHash := digest(in.Code)
	challenge := sha256.Sum256([]byte(in.Verifier))
	var user, session string
	err = tx.QueryRow(ctx, `SELECT source_user_id::text,source_session_id::text FROM note_browser_handoffs
		WHERE code_hash=$1 AND challenge=$2 AND expires_at>clock_timestamp()`, codeHash, challenge[:]).Scan(&user, &session)
	if err == pgx.ErrNoRows {
		return invalidHandoff()
	}
	if err != nil {
		return err
	}
	// Match the lock order used by password changes and device revocation.
	var email string
	var verified bool
	var version int64
	err = tx.QueryRow(ctx, "SELECT email,email_verified,auth_version FROM users WHERE id=$1 FOR SHARE", user).Scan(&email, &verified, &version)
	if err == pgx.ErrNoRows {
		return invalidHandoff()
	}
	if err != nil {
		return err
	}
	var active bool
	err = tx.QueryRow(ctx, `SELECT true FROM sessions WHERE id=$1 AND user_id=$2 AND app_id='kimnotes'
		AND client_kind='desktop' AND protocol_version=2 AND revoked_at IS NULL
		AND expires_at>clock_timestamp() AND auth_version=$3 FOR SHARE`, session, user, version).Scan(&active)
	if err == pgx.ErrNoRows {
		return invalidHandoff()
	}
	if err != nil {
		return err
	}
	tag, err := tx.Exec(ctx, `DELETE FROM note_browser_handoffs
		WHERE code_hash=$1 AND challenge=$2 AND expires_at>clock_timestamp()`, codeHash, challenge[:])
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return invalidHandoff()
	}
	return a.finishLogin(w, r, tx, user, email, verified, "", "", version)
}
