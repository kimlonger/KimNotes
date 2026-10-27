package main

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"encoding/base64"
	"fmt"
	"github.com/jackc/pgx/v5"
	"math/big"
	"net"
	"net/http"
	"net/mail"
	"net/smtp"
	"strings"
	"time"
)

func encodeSubject(subject string) string { return base64.StdEncoding.EncodeToString([]byte(subject)) }

type credentials struct {
	Email      string `json:"email"`
	Password   string `json:"password"`
	DeviceName string `json:"deviceName"`
	DeviceID   string `json:"deviceId,omitempty"`
}

// Native password login intentionally has no CAPTCHA. It shares the same
// password verifier, per-IP/per-account limits and app-scoped sessions.
func (a *App) desktopLogin(w http.ResponseWriter, r *http.Request) error {
	r.Header.Set("X-Kim-Client", "desktop")
	return a.login(w, r)
}

func normalizeEmail(s string) (string, error) {
	s = strings.ToLower(strings.TrimSpace(s))
	p, e := mail.ParseAddress(s)
	if e != nil || p.Address != s || len(s) > 254 || !strings.Contains(s, ".") {
		return "", fail(400, "invalid_email", "请输入有效的邮箱地址。")
	}
	return s, nil
}
func validPassword(s string) error {
	if len([]rune(s)) < 8 || len([]rune(s)) > 128 || len(s) > 512 {
		return fail(400, "invalid_password", "密码需要 8–128 位字符。")
	}
	return nil
}
func (a *App) register(w http.ResponseWriter, r *http.Request) error {
	if e := a.existingApp(r); e != nil {
		return e
	}
	var in credentials
	if e := decode(w, r, &in); e != nil {
		return e
	}
	email, e := normalizeEmail(in.Email)
	if e != nil {
		return e
	}
	if e = validPassword(in.Password); e != nil {
		return e
	}
	if e = a.authLimit(r, email); e != nil {
		return e
	}
	hash := hashPassword(in.Password)
	id := newID()
	app := r.PathValue("app")
	if !a.config.RequireVerification {
		tx, e := a.db.Begin(r.Context())
		if e != nil {
			return e
		}
		defer tx.Rollback(r.Context())
		tag, e := tx.Exec(r.Context(), "INSERT INTO users(id,email,password_hash) VALUES($1,$2,$3) ON CONFLICT(email) DO NOTHING", id, email, hash)
		if e != nil {
			return e
		}
		if tag.RowsAffected() == 0 {
			return fail(409, "account_exists", "该邮箱已有账号，请登录或找回密码。")
		}
		if _, e = tx.Exec(r.Context(), "INSERT INTO user_apps(app_id,user_id) VALUES($1,$2)", app, id); e != nil {
			return e
		}
		if e = tx.Commit(r.Context()); e != nil {
			return e
		}
		return a.createSession(w, r, id, email, false, in.DeviceName, in.DeviceID, hash)
	}
	n, e := rand.Int(rand.Reader, big.NewInt(1000000))
	if e != nil {
		return e
	}
	code := fmt.Sprintf("%06d", n.Int64())
	var exists bool
	if e = a.db.QueryRow(r.Context(), "SELECT EXISTS(SELECT 1 FROM users WHERE email=$1)", email).Scan(&exists); e != nil {
		return e
	}
	if !exists {
		_, e = a.db.Exec(r.Context(), "INSERT INTO auth_challenges(id,app_id,email,purpose,token_hash,password_hash,expires_at) VALUES($1,$2,$3,'register',$4,$5,now()+interval '10 minutes')", id, app, email, digest(id+":"+code), hash)
		if e != nil {
			return e
		}
		if e = a.sendMail(r.Context(), email, "小羊账号 · 注册验证码", "你的注册验证码是："+code+"\r\n10 分钟内有效。如非本人操作，请忽略此邮件。"); e != nil {
			return fail(503, "mail_unavailable", "验证邮件暂时无法发送，请稍后重试。")
		}
	}
	writeJSON(w, 202, map[string]any{"verificationRequired": true, "challengeId": id, "message": "若邮箱可注册，验证码将发送至该邮箱。已有账号请直接登录。"})
	return nil
}
func (a *App) verify(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		ChallengeID string `json:"challengeId"`
		Code        string `json:"code"`
		DeviceName  string `json:"deviceName"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if !validID(in.ChallengeID) || len(in.Code) != 6 {
		return fail(400, "invalid_code", "验证码无效或已过期。")
	}
	if e := a.authLimit(r, in.ChallengeID); e != nil {
		return e
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var email, hash, want string
	var attempts int
	e = tx.QueryRow(r.Context(), "UPDATE auth_challenges SET attempts=attempts+1 WHERE id=$1 AND app_id=$2 AND purpose='register' AND expires_at>now() AND attempts<5 RETURNING email,password_hash,token_hash,attempts", in.ChallengeID, r.PathValue("app")).Scan(&email, &hash, &want, &attempts)
	if e == pgx.ErrNoRows {
		return fail(400, "invalid_code", "验证码无效或已过期。")
	}
	if e != nil {
		return e
	}
	if !secureEqual(want, digest(in.ChallengeID+":"+in.Code)) {
		if e = tx.Commit(r.Context()); e != nil {
			return e
		}
		return fail(400, "invalid_code", "验证码无效或已过期。")
	}
	id := newID()
	tag, e := tx.Exec(r.Context(), "INSERT INTO users(id,email,password_hash,email_verified) VALUES($1,$2,$3,true) ON CONFLICT(email) DO NOTHING", id, email, hash)
	if e != nil {
		return e
	}
	if tag.RowsAffected() == 0 {
		return fail(409, "account_exists", "该邮箱已有账号，请直接登录。")
	}
	if _, e = tx.Exec(r.Context(), "INSERT INTO user_apps(app_id,user_id) VALUES($1,$2)", r.PathValue("app"), id); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "DELETE FROM auth_challenges WHERE email=$1 AND purpose='register'", email); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	return a.createSession(w, r, id, email, true, in.DeviceName, "", hash)
}
func (a *App) login(w http.ResponseWriter, r *http.Request) error {
	if e := a.existingApp(r); e != nil {
		return e
	}
	var in credentials
	if e := decode(w, r, &in); e != nil {
		return e
	}
	email, e := normalizeEmail(in.Email)
	if e != nil {
		return e
	}
	if len(in.Password) > 512 {
		return fail(400, "invalid_password", "密码过长。")
	}
	if e = a.authLimit(r, email); e != nil {
		return e
	}
	var id, hash string
	var verified bool
	e = a.db.QueryRow(r.Context(), "SELECT id::text,password_hash,email_verified FROM users WHERE email=$1", email).Scan(&id, &hash, &verified)
	if e != nil && e != pgx.ErrNoRows {
		return e
	}
	if e == pgx.ErrNoRows {
		hash = a.dummyHash
	}
	ok := checkPassword(in.Password, hash)
	if !ok || id == "" {
		return fail(401, "invalid_credentials", "邮箱或密码不正确。")
	}
	if _, e = a.db.Exec(r.Context(), "INSERT INTO user_apps(app_id,user_id) VALUES($1,$2) ON CONFLICT DO NOTHING", r.PathValue("app"), id); e != nil {
		return e
	}
	return a.createSession(w, r, id, email, verified, in.DeviceName, in.DeviceID, hash)
}
func (a *App) createSession(w http.ResponseWriter, r *http.Request, id, email string, verified bool, device, deviceID, expectedHash string) error {
	return a.createManagedSession(w, r, id, email, verified, device, deviceID, expectedHash)
}

func (a *App) me(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	writeJSON(w, 200, map[string]any{"user": map[string]any{"id": p.UserID, "email": p.Email, "emailVerified": p.Verified}, "appId": p.AppID, "session": map[string]any{"id": p.SessionID, "clientKind": p.ClientKind, "idleExpiresAt": p.IdleExpires, "absoluteExpiresAt": p.AbsoluteExpires, "protocolVersion": p.Protocol}})
	return nil
}
func (a *App) logout(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if _, e := a.revokeDevice(r.Context(), p.UserID, p.SessionID, "logout"); e != nil {
		return e
	}
	a.cookie(w, p.AppID, "", -1)
	writeJSON(w, 200, map[string]bool{"ok": true})
	return nil
}
func (a *App) changePassword(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	var in struct {
		Current string `json:"currentPassword"`
		Next    string `json:"newPassword"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if e := validPassword(in.Next); e != nil {
		return e
	}
	if e := a.authLimit(r, p.Email); e != nil {
		return e
	}
	if len(in.Current) > 512 {
		return fail(400, "invalid_password", "密码过长。")
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var hash string
	if e = tx.QueryRow(r.Context(), "SELECT password_hash FROM users WHERE id=$1 FOR UPDATE", p.UserID).Scan(&hash); e != nil {
		return e
	}
	if !checkPassword(in.Current, hash) {
		return fail(401, "invalid_credentials", "当前密码不正确。")
	}
	if _, e = tx.Exec(r.Context(), "UPDATE users SET password_hash=$2,auth_version=auth_version+1,password_changed_at=now() WHERE id=$1", p.UserID, hashPassword(in.Next)); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "DELETE FROM sessions WHERE user_id=$1", p.UserID); e != nil {
		return e
	}
	if e = authEvent(r.Context(), tx, p.UserID, p.AppID, p.SessionID, "password_changed"); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	a.cookie(w, p.AppID, "", -1)
	writeJSON(w, 200, map[string]string{"message": "密码已修改，请重新登录。"})
	return nil
}
func (a *App) requestReset(w http.ResponseWriter, r *http.Request) error {
	if e := a.existingApp(r); e != nil {
		return e
	}
	if !a.config.mailReady() {
		return fail(503, "mail_not_configured", "找回密码邮件服务尚未配置。")
	}
	var in struct {
		Email string `json:"email"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	email, e := normalizeEmail(in.Email)
	if e != nil {
		return e
	}
	if e = a.authLimit(r, email); e != nil {
		return e
	}
	var exists bool
	if e = a.db.QueryRow(r.Context(), "SELECT EXISTS(SELECT 1 FROM users WHERE email=$1)", email).Scan(&exists); e != nil {
		return e
	}
	if exists {
		id := newID()
		token := randomToken()
		_, e = a.db.Exec(r.Context(), "INSERT INTO auth_challenges(id,app_id,email,purpose,token_hash,expires_at) VALUES($1,$2,$3,'reset',$4,now()+interval '30 minutes')", id, r.PathValue("app"), email, digest(token))
		if e != nil {
			return e
		}
		link := a.config.PublicOrigin + "/login/?mode=reset#" + id + ":" + token
		if e = a.sendMail(r.Context(), email, "小羊账号 · 重置密码", "请打开以下链接设置新密码（30 分钟内有效）：\r\n"+link+"\r\n如非本人操作，请忽略此邮件。"); e != nil {
			return fail(503, "mail_unavailable", "邮件暂时无法发送，请稍后重试。")
		}
	}
	writeJSON(w, 202, map[string]string{"message": "如果该邮箱已注册，你将收到重置密码邮件。"})
	return nil
}
func (a *App) completeReset(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		ChallengeID string `json:"challengeId"`
		Token       string `json:"token"`
		Password    string `json:"password"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if !validID(in.ChallengeID) || len(in.Token) != 43 {
		return fail(400, "invalid_token", "重置链接无效或已过期。")
	}
	if e := validPassword(in.Password); e != nil {
		return e
	}
	if e := a.authLimit(r, in.ChallengeID); e != nil {
		return e
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var email string
	e = tx.QueryRow(r.Context(), "DELETE FROM auth_challenges WHERE id=$1 AND app_id=$2 AND purpose='reset' AND token_hash=$3 AND expires_at>now() RETURNING email", in.ChallengeID, r.PathValue("app"), digest(in.Token)).Scan(&email)
	if e == pgx.ErrNoRows {
		return fail(400, "invalid_token", "重置链接无效或已过期。")
	}
	if e != nil {
		return e
	}
	var user string
	if e = tx.QueryRow(r.Context(), "UPDATE users SET password_hash=$2,email_verified=true,auth_version=auth_version+1,password_changed_at=now() WHERE email=$1 RETURNING id::text", email, hashPassword(in.Password)).Scan(&user); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "DELETE FROM sessions WHERE user_id=$1", user); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "INSERT INTO auth_events(user_id,app_id,event) VALUES($1,$2,'password_reset')", user, r.PathValue("app")); e != nil {
		return e
	}
	if _, e = tx.Exec(r.Context(), "DELETE FROM auth_challenges WHERE email=$1 AND purpose='reset'", email); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]string{"message": "密码已重置，请使用新密码登录。"})
	return nil
}
func (a *App) smtpSend(ctx context.Context, to, subject, body string) error {
	c := a.config
	from, e := mail.ParseAddress(c.MailFrom)
	if e != nil {
		return e
	}
	address := net.JoinHostPort(c.SMTPHost, c.SMTPPort)
	d := net.Dialer{Timeout: 10 * time.Second}
	var conn net.Conn
	if c.SMTPPort == "465" {
		conn, e = tls.DialWithDialer(&d, "tcp", address, &tls.Config{ServerName: c.SMTPHost, MinVersion: tls.VersionTLS12})
	} else {
		conn, e = d.DialContext(ctx, "tcp", address)
	}
	if e != nil {
		return e
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(20 * time.Second))
	client, e := smtp.NewClient(conn, c.SMTPHost)
	if e != nil {
		return e
	}
	defer client.Close()
	if c.SMTPPort != "465" {
		if e = client.StartTLS(&tls.Config{ServerName: c.SMTPHost, MinVersion: tls.VersionTLS12}); e != nil {
			return e
		}
	}
	if c.SMTPUser != "" {
		if e = client.Auth(smtp.PlainAuth("", c.SMTPUser, c.SMTPPassword, c.SMTPHost)); e != nil {
			return e
		}
	}
	if e = client.Mail(from.Address); e != nil {
		return e
	}
	if e = client.Rcpt(to); e != nil {
		return e
	}
	wc, e := client.Data()
	if e != nil {
		return e
	}
	_, e = fmt.Fprintf(wc, "From: %s\r\nTo: %s\r\nSubject: =?UTF-8?B?%s?=\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n%s\r\n", c.MailFrom, to, encodeSubject(subject), body)
	if e != nil {
		return e
	}
	if e = wc.Close(); e != nil {
		return e
	}
	return client.Quit()
}
