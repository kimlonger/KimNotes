package main

import (
	"context"
	_ "embed"
	"github.com/jackc/pgx/v5/pgxpool"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

//go:embed migrations/001_initial.sql
var schema string

//go:embed migrations/002_sessions.sql
var sessionSchema string

//go:embed migrations/003_auth_events.sql
var eventSchema string

//go:embed migrations/004_main_account.sql
var mainAccountSchema string

//go:embed migrations/005_content_catalog.sql
var contentSchema string

//go:embed migrations/006_guitar.sql
var guitarSchema string

//go:embed guitar-seed.json
var guitarSeed []byte

//go:embed content-seed.json
var contentSeed []byte

func main() {
	mode := ""
	if len(os.Args) > 1 {
		mode = os.Args[1]
	}
	c, err := loadModeConfig(mode)
	if err != nil {
		slog.Error("configuration", "error", err)
		os.Exit(1)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	pc, err := pgxpool.ParseConfig(c.DatabaseURL)
	if err != nil {
		slog.Error("invalid database configuration")
		os.Exit(1)
	}
	pc.MaxConns = 8
	pc.MinConns = 0
	pc.ConnConfig.RuntimeParams["search_path"] = "kimcloud,public"
	db, err := pgxpool.NewWithConfig(ctx, pc)
	if err != nil {
		slog.Error("database pool initialization failed")
		os.Exit(1)
	}
	defer db.Close()
	if len(os.Args) > 1 && os.Args[1] == "maintenance" {
		// A separate short-lived migration runner can listen without requiring the
		// new schema, while the existing production API keeps serving requests.
		_ = http.ListenAndServe(c.Address, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path != "/healthz" {
				http.NotFound(w, r)
				return
			}
			writeJSON(w, 200, map[string]string{"status": "migration-helper"})
		}))
		return
	}
	if len(os.Args) > 1 && os.Args[1] == "migrate" {
		boot, cancel := context.WithTimeout(ctx, 90*time.Second)
		defer cancel()
		if err = migrate(boot, db); err != nil {
			slog.Error("database migration failed", "error", err)
			os.Exit(1)
		}
		slog.Info("database migrations applied")
		return
	}
	if len(os.Args) > 1 && os.Args[1] == "migration-status" {
		check, cancel := context.WithTimeout(ctx, 15*time.Second)
		defer cancel()
		var applied bool
		err = db.QueryRow(check, "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version='005_content_catalog') AND to_regclass('refresh_tokens') IS NOT NULL AND to_regclass('web_cookie_aliases') IS NOT NULL AND to_regclass('auth_events') IS NOT NULL AND to_regclass('main_account_follows') IS NOT NULL AND to_regclass('main_account_totals') IS NOT NULL AND to_regclass('main_account_entries') IS NOT NULL AND to_regclass('content_works') IS NOT NULL AND to_regclass('content_articles') IS NOT NULL AND to_regclass('content_updates') IS NOT NULL AND EXISTS(SELECT 1 FROM applications WHERE id='main') AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='applications' AND column_name='public_origin')").Scan(&applied)
		if err == nil && applied {
			err = db.QueryRow(check, "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version='006_guitar') AND to_regclass('guitar_courses') IS NOT NULL AND to_regclass('guitar_lessons') IS NOT NULL AND to_regclass('guitar_scores') IS NOT NULL AND to_regclass('guitar_visits') IS NOT NULL").Scan(&applied)
		}
		if err != nil || !applied {
			slog.Error("database migration incomplete")
			os.Exit(1)
		}
		slog.Info("database migration verified", "version", "006_guitar")
		return
	}
	if mode == "content-import" {
		replace := false
		for _, argument := range os.Args[2:] {
			if argument != "--replace" || replace {
				slog.Error("content-import only accepts an optional --replace flag")
				os.Exit(1)
			}
			replace = true
		}
		boot, cancel := context.WithTimeout(ctx, 90*time.Second)
		defer cancel()
		result, err := importContent(boot, db, contentSeed, replace)
		if err != nil {
			slog.Error("content import failed", "error", err)
			os.Exit(1)
		}
		slog.Info("content imported", "projects", result.Projects, "articles", result.Articles, "updates", result.Updates, "replace", replace)
		return
	}
	if mode == "guitar-import" {
		raw, replace, inputErr := guitarImportInput(os.Args[2:])
		if inputErr != nil {
			slog.Error("guitar import arguments", "error", inputErr)
			os.Exit(1)
		}
		if err := importGuitarContent(ctx, &App{db: db}, raw, replace); err != nil {
			slog.Error("guitar import failed", "error", err)
			os.Exit(1)
		}
		slog.Info("guitar content imported", "replace", replace)
		return
	}
	if mode == "guitar-review" {
		if err := reviewGuitar(ctx, db, os.Args[2:]); err != nil {
			slog.Error("guitar review failed", "error", err)
			os.Exit(1)
		}
		return
	}
	a := newApp(c, db)
	go a.maintainSessionCaches(ctx)
	srv := &http.Server{Addr: c.Address, Handler: a.routes(), ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 45 * time.Second, WriteTimeout: 60 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 32 << 10}
	go func() {
		<-ctx.Done()
		shutdown, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		_ = srv.Shutdown(shutdown)
	}()
	slog.Info("kimcloud listening", "address", c.Address, "emailVerification", c.RequireVerification)
	if err = srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		slog.Error("HTTP server failed", "error", err)
		os.Exit(1)
	}
}

func migrate(ctx context.Context, db *pgxpool.Pool) error {
	tx, err := db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, "SELECT pg_advisory_xact_lock(725194138)"); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, schema); err != nil {
		return err
	}
	if _, err = tx.Exec(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations(version text PRIMARY KEY,applied_at timestamptz NOT NULL DEFAULT now()); INSERT INTO schema_migrations(version) VALUES('001_initial') ON CONFLICT DO NOTHING`); err != nil {
		return err
	}
	for _, m := range []struct{ version, source string }{{"002_sessions", sessionSchema}, {"003_auth_events", eventSchema}, {"004_main_account", mainAccountSchema}, {"005_content_catalog", contentSchema}, {"006_guitar", guitarSchema}} {
		var applied bool
		if err = tx.QueryRow(ctx, "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=$1)", m.version).Scan(&applied); err != nil {
			return err
		}
		if !applied {
			if _, err = tx.Exec(ctx, m.source); err != nil {
				return err
			}
			if _, err = tx.Exec(ctx, "INSERT INTO schema_migrations(version) VALUES($1)", m.version); err != nil {
				return err
			}
		}
	}
	return tx.Commit(ctx)
}
