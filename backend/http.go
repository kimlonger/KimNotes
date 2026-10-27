package main

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"io"
	"log/slog"
	"mime"
	"net"
	"net/http"
	"time"
)

type App struct {
	config    Config
	db        *pgxpool.Pool
	dummyHash string
	sendMail  func(context.Context, string, string, string) error
}
type principal struct {
	UserID, AppID, Email, TokenHash, ClientKind string
	Verified                                    bool
	SessionID                                   string
	Protocol                                    int
	IdleExpires, AbsoluteExpires                *time.Time
}
type principalKey struct{}
type apiError struct {
	Status        int
	Code, Message string
}

func (e *apiError) Error() string                 { return e.Code }
func fail(status int, code, message string) error { return &apiError{status, code, message} }
func newApp(c Config, db *pgxpool.Pool) *App {
	a := &App{config: c, db: db, dummyHash: hashPassword(randomToken())}
	a.sendMail = a.smtpSend
	return a
}
func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func (a *App) endpoint(fn func(http.ResponseWriter, *http.Request) error) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := fn(w, r); err != nil {
			var e *apiError
			if !errors.As(err, &e) {
				if errors.Is(err, pgx.ErrNoRows) {
					e = &apiError{404, "not_found", "内容不存在或已不可用。"}
				} else {
					slog.Error("request failed", "method", r.Method, "path", r.URL.Path, "error", err)
					e = &apiError{500, "internal_error", "服务暂时不可用，请稍后重试。"}
				}
			}
			writeJSON(w, e.Status, map[string]string{"code": e.Code, "message": e.Message})
		}
	}
}
func decode(w http.ResponseWriter, r *http.Request, v any) error {
	media, _, _ := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if media != "application/json" {
		return fail(415, "json_required", "请使用 JSON 请求。")
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4<<20)
	d := json.NewDecoder(r.Body)
	d.DisallowUnknownFields()
	if err := d.Decode(v); err != nil {
		return fail(400, "invalid_json", "请求内容无效或过大。")
	}
	if d.Decode(&struct{}{}) != io.EOF {
		return fail(400, "invalid_json", "请求应只有一个 JSON 对象。")
	}
	return nil
}
func (a *App) routes() http.Handler {
	m := http.NewServeMux()
	p := "/api/v1/apps/{app}"
	m.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) { writeJSON(w, 200, map[string]string{"status": "ok"}) })
	m.HandleFunc("GET /readyz", a.endpoint(func(w http.ResponseWriter, r *http.Request) error {
		if e := a.db.Ping(r.Context()); e != nil {
			return fail(503, "unavailable", "数据库连接不可用。")
		}
		writeJSON(w, 200, map[string]string{"status": "ready"})
		return nil
	}))
	m.HandleFunc("GET "+p+"/config", a.endpoint(a.clientConfig))
	m.HandleFunc("POST "+p+"/auth/register", a.endpoint(a.register))
	m.HandleFunc("POST "+p+"/auth/verify", a.endpoint(a.verify))
	m.HandleFunc("POST "+p+"/auth/login", a.endpoint(a.login))
	m.HandleFunc("POST "+p+"/auth/desktop/login", a.endpoint(a.desktopLogin))
	m.HandleFunc("POST "+p+"/auth/desktop/refresh", a.endpoint(a.refreshDesktop))
	m.HandleFunc("POST "+p+"/auth/desktop/upgrade", a.endpoint(a.upgradeDesktop))
	m.HandleFunc("POST "+p+"/auth/desktop/logout", a.endpoint(a.logoutDesktop))
	m.HandleFunc("POST "+p+"/auth/reset", a.endpoint(a.requestReset))
	m.HandleFunc("POST "+p+"/auth/reset/complete", a.endpoint(a.completeReset))
	m.HandleFunc("GET "+p+"/auth/me", a.require(a.me))
	m.HandleFunc("POST "+p+"/auth/logout", a.require(a.logout))
	m.HandleFunc("POST "+p+"/auth/password", a.require(a.changePassword))
	m.HandleFunc("POST "+p+"/auth/activity", a.require(a.activity))
	m.HandleFunc("GET "+p+"/auth/sessions", a.require(a.listSessions))
	m.HandleFunc("DELETE "+p+"/auth/sessions/{session}", a.require(a.revokeSession))
	m.HandleFunc("POST "+p+"/auth/logout-all", a.require(a.logoutAll))
	m.HandleFunc("GET "+p+"/sync/pull", a.require(a.pull))
	m.HandleFunc("POST "+p+"/sync/push", a.require(a.push))
	m.HandleFunc("GET "+p+"/notes/{note}/versions", a.require(a.versions))
	m.HandleFunc("POST "+p+"/attachments", a.require(a.upload))
	m.HandleFunc("GET "+p+"/attachments/{attachment}", a.require(a.download))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "private, no-store")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		if r.URL.Path != "/healthz" && r.URL.Path != "/readyz" && !a.config.Development && !secureEqual(r.Header.Get("X-Kim-Gateway"), a.config.GatewaySecret) {
			writeJSON(w, 403, map[string]string{"code": "gateway_required", "message": "请通过官网访问服务。"})
			return
		}
		if r.Method != "GET" && r.Method != "HEAD" {
			origin := r.Header.Get("Origin")
			desktop := r.Header.Get("X-Kim-Client") == "desktop"
			if origin != a.config.PublicOrigin && !(origin == "" && desktop) {
				writeJSON(w, 403, map[string]string{"code": "invalid_origin", "message": "请求来源无效。"})
				return
			}
		}
		ctx, cancel := context.WithTimeout(r.Context(), 40*time.Second)
		defer cancel()
		m.ServeHTTP(w, r.WithContext(ctx))
	})
}
func (a *App) existingApp(r *http.Request) error {
	var id string
	err := a.db.QueryRow(r.Context(), "SELECT id FROM applications WHERE id=$1", r.PathValue("app")).Scan(&id)
	if errors.Is(err, pgx.ErrNoRows) {
		return fail(404, "unknown_app", "应用不存在。")
	}
	return err
}
func (a *App) clientConfig(w http.ResponseWriter, r *http.Request) error {
	if e := a.existingApp(r); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"appId": r.PathValue("app"), "emailVerification": a.config.RequireVerification, "passwordReset": a.config.mailReady(), "syncIntervalSeconds": 30, "webSessionIdleSeconds": 1800, "webSessionAbsoluteSeconds": 28800, "desktopAccessSeconds": 900, "desktopRefreshIdleSeconds": 15552000, "inlineImageAttachmentsVersion": 1})
	return nil
}
func (a *App) require(fn func(http.ResponseWriter, *http.Request) error) http.HandlerFunc {
	return a.endpoint(func(w http.ResponseWriter, r *http.Request) error {
		p, err := a.authenticate(r)
		if err != nil {
			return err
		}
		if expected := r.Header.Get("X-Kim-Expected-User"); expected != "" && expected != p.UserID {
			return fail(409, "account_changed", "账号已在其他页面切换，请确认当前账号后继续。")
		}
		return fn(w, r.WithContext(context.WithValue(r.Context(), principalKey{}, p)))
	})
}
func identity(r *http.Request) principal { return r.Context().Value(principalKey{}).(principal) }
func (a *App) cookieName(app string) string {
	if a.config.Development {
		return "kim_session_" + app
	}
	return "__Host-kim_session_" + app
}
func (a *App) cookie(w http.ResponseWriter, app, token string, maxAge int) {
	http.SetCookie(w, &http.Cookie{Name: a.cookieName(app), Value: token, Path: "/", Secure: !a.config.Development, HttpOnly: true, SameSite: http.SameSiteLaxMode, MaxAge: maxAge})
}
func (a *App) limited(r *http.Request, key string, max int, window time.Duration) error {
	var count int
	e := a.db.QueryRow(r.Context(), `INSERT INTO rate_limits(key,window_start,count) VALUES($1,now(),1) ON CONFLICT(key) DO UPDATE SET count=CASE WHEN rate_limits.window_start<now()-$2*interval '1 second' THEN 1 ELSE rate_limits.count+1 END,window_start=CASE WHEN rate_limits.window_start<now()-$2*interval '1 second' THEN now() ELSE rate_limits.window_start END RETURNING count`, digest(key), window.Seconds()).Scan(&count)
	if e != nil {
		return e
	}
	if count > max {
		return fail(429, "rate_limited", "操作太频繁，请稍后再试。")
	}
	return nil
}
func (a *App) authLimit(r *http.Request, email string) error {
	ip := r.Header.Get("X-Kim-Client-IP")
	if ip == "" {
		ip, _, _ = net.SplitHostPort(r.RemoteAddr)
	}
	if e := a.limited(r, "auth:ip:"+ip, 40, 15*time.Minute); e != nil {
		return e
	}
	return a.limited(r, "auth:email:"+email, 12, 15*time.Minute)
}
func (a *App) sessionLimit(r *http.Request) error {
	ip := r.Header.Get("X-Kim-Client-IP")
	if ip == "" {
		ip, _, _ = net.SplitHostPort(r.RemoteAddr)
	}
	return a.limited(r, "session:ip:"+ip, 120, time.Minute)
}
