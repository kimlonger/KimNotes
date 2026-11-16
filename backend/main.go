package main

import (
	"context"
	_ "embed"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"slices"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

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
	if mode == "migration-ledger" {
		check, cancel := context.WithTimeout(ctx, 15*time.Second)
		defer cancel()
		applied, err := appliedMigrations(check, db)
		if err != nil {
			slog.Error("database migration ledger unavailable")
			os.Exit(1)
		}
		versions := make([]string, 0, len(applied))
		for version := range applied {
			versions = append(versions, version)
		}
		slices.Sort(versions)
		for _, version := range versions {
			slog.Info("database migration", "version", version)
		}
		return
	}
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
		migrations, err := loadMigrations()
		if err == nil {
			err = verifyMigrations(check, db, migrations)
		}
		if err != nil {
			slog.Error("database migration incomplete", "error", err)
			os.Exit(1)
		}
		slog.Info("database migration verified", "version", migrations[len(migrations)-1].version)
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
