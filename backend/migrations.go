package main

import (
	"context"
	"embed"
	"fmt"
	"io/fs"
	"regexp"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

//go:embed migrations/*.sql
var migrationFiles embed.FS

var migrationFilename = regexp.MustCompile(`^([0-9]{3})_[a-z0-9_]+\.sql$`)

type migration struct {
	version string
	source  string
}

func loadMigrations() ([]migration, error) {
	entries, err := fs.ReadDir(migrationFiles, "migrations")
	if err != nil {
		return nil, err
	}
	if len(entries) == 0 {
		return nil, fmt.Errorf("no database migrations embedded")
	}
	result := make([]migration, 0, len(entries))
	for index, entry := range entries {
		name := entry.Name()
		match := migrationFilename.FindStringSubmatch(name)
		if len(match) != 2 || match[1] != fmt.Sprintf("%03d", index+1) {
			return nil, fmt.Errorf("unexpected migration filename %q", name)
		}
		source, err := migrationFiles.ReadFile("migrations/" + name)
		if err != nil {
			return nil, err
		}
		result = append(result, migration{strings.TrimSuffix(name, ".sql"), string(source)})
	}
	return result, nil
}

type migrationReader interface {
	Query(context.Context, string, ...any) (pgx.Rows, error)
	QueryRow(context.Context, string, ...any) pgx.Row
}

func appliedMigrations(ctx context.Context, db migrationReader) (map[string]bool, error) {
	rows, err := db.Query(ctx, "SELECT version FROM kimcloud.schema_migrations")
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	applied := make(map[string]bool)
	for rows.Next() {
		var version string
		if err := rows.Scan(&version); err != nil {
			return nil, err
		}
		applied[version] = true
	}
	return applied, rows.Err()
}

func verifyMigrations(ctx context.Context, db migrationReader, migrations []migration) error {
	applied, err := appliedMigrations(ctx, db)
	if err != nil {
		return err
	}
	for _, migration := range migrations {
		if !applied[migration.version] {
			return fmt.Errorf("missing database migration %s", migration.version)
		}
	}
	// Keep the earlier deployment checks for critical objects in addition to
	// checking every ledger entry. A recorded migration can drift manually.
	var schemaIntact bool
	err = db.QueryRow(ctx, `
		SELECT NOT EXISTS (
			SELECT 1 FROM unnest(ARRAY[
				'refresh_tokens','web_cookie_aliases','auth_events','main_account_follows',
				'main_account_totals','main_account_entries','content_works','content_articles',
				'content_updates','guitar_scores','guitar_visits','guitar_artists'
			]) AS required(name)
			WHERE to_regclass('kimcloud.' || name) IS NULL
		) AND EXISTS (SELECT 1 FROM kimcloud.applications WHERE id='main')
		AND 3 = (
			SELECT count(*) FROM pg_attribute
			WHERE attnum > 0 AND NOT attisdropped AND (
				(attrelid = to_regclass('kimcloud.applications') AND attname = 'public_origin') OR
				(attrelid = to_regclass('kimcloud.guitar_scores') AND attname IN ('difficulty_stars','artist_id'))
			)
		)`).Scan(&schemaIntact)
	if err != nil {
		return err
	}
	if !schemaIntact {
		return fmt.Errorf("required database objects are missing")
	}
	return nil
}

func migrate(ctx context.Context, db *pgxpool.Pool) error {
	migrations, err := loadMigrations()
	if err != nil {
		return err
	}
	tx, err := db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	for _, statement := range []string{
		"SELECT pg_advisory_xact_lock(725194138)",
		"CREATE SCHEMA IF NOT EXISTS kimcloud",
		"SET LOCAL search_path TO kimcloud, public",
		`CREATE TABLE IF NOT EXISTS kimcloud.schema_migrations (
			version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now()
		)`,
	} {
		if _, err := tx.Exec(ctx, statement); err != nil {
			return err
		}
	}
	applied, err := appliedMigrations(ctx, tx)
	if err != nil {
		return err
	}
	for _, migration := range migrations {
		if applied[migration.version] {
			continue
		}
		if _, err := tx.Exec(ctx, migration.source); err != nil {
			return fmt.Errorf("apply %s: %w", migration.version, err)
		}
		if _, err := tx.Exec(ctx, "INSERT INTO kimcloud.schema_migrations(version) VALUES($1)", migration.version); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}
