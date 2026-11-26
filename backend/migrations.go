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
				'content_updates','guitar_scores','guitar_visits','guitar_artists',
				'note_versions','operations','note_versions_stream_key','guitar_practice_sessions',
				'guitar_score_files','guitar_members','guitar_reading_progress','sessions_web_cookie_hash_key',
				'guitar_practice_history_order',
				'account_point_wallets','account_point_events','note_todos','note_profiles','note_pickup_codes'
			]) AS required(name)
			WHERE to_regclass('kimcloud.' || name) IS NULL
		) AND EXISTS (SELECT 1 FROM kimcloud.applications WHERE id='main')
		AND NOT EXISTS (
			SELECT 1 FROM unnest(ARRAY[
				'note_changes','guitar_courses','guitar_chapters','guitar_lessons',
				'guitar_progress','guitar_questions','guitar_replies','auth_policy',
				'guitar_practice_logs','guitar_practice_task_sessions','guitar_upload_pages','guitar_favorites',
				'guitar_practice_preferences','guitar_score_pages'
			]) AS retired(name)
			WHERE to_regclass('kimcloud.' || name) IS NOT NULL
		)
		AND 28 = (
			SELECT count(*) FROM pg_attribute
			WHERE attnum > 0 AND NOT attisdropped AND (
				(attrelid = to_regclass('kimcloud.applications') AND attname = 'public_origin') OR
				(attrelid = to_regclass('kimcloud.auth_challenges') AND attname IN ('note_nickname','note_avatar_id') AND attnotnull) OR
				(attrelid = to_regclass('kimcloud.auth_challenges') AND attname = 'note_avatar_png') OR
				(attrelid = to_regclass('kimcloud.note_profiles') AND attname IN ('avatar_png','avatar_version')) OR
				(attrelid = to_regclass('kimcloud.guitar_scores') AND attname IN ('difficulty_stars','artist_id','withdrawn_upload_owner_id','file_format','difficulty_source')) OR
				(attrelid = to_regclass('kimcloud.guitar_members') AND attname IN ('weekly_minutes','weekly_days','first_tuned','equipped_title')) OR
				(attrelid = to_regclass('kimcloud.note_versions') AND attname = 'sequence' AND attnotnull) OR
				(attrelid = to_regclass('kimcloud.operations') AND attname IN ('result_note_id','result_revision')) OR
				(attrelid = to_regclass('kimcloud.guitar_practice_sessions') AND attname = 'record_kind') OR
				(attrelid = to_regclass('kimcloud.guitar_score_files') AND attname IN ('score_id','file_no') AND attnotnull) OR
				(attrelid = to_regclass('kimcloud.guitar_reading_progress') AND attname IN ('favorite_at','reading_started')) OR
				(attrelid = to_regclass('kimcloud.sessions') AND attname = 'token_hash' AND NOT attnotnull) OR
				(attrelid = to_regclass('kimcloud.account_point_events') AND attname = 'event_kind' AND attnotnull) OR
				(attrelid = to_regclass('kimcloud.note_todos') AND attname IN ('priority','notes','pinned') AND attnotnull)
			)
		) AND EXISTS (
			SELECT 1 FROM pg_constraint
			WHERE conrelid = to_regclass('kimcloud.notes') AND conname = 'notes_current_version' AND convalidated
		) AND NOT EXISTS (
			SELECT 1 FROM pg_attribute WHERE attnum > 0 AND NOT attisdropped AND (
				(attrelid = to_regclass('kimcloud.notes') AND attname = 'document') OR
				(attrelid = to_regclass('kimcloud.refresh_tokens') AND attname = 'replaced_by')
			)
		) AND EXISTS (
			SELECT 1 FROM pg_constraint
			WHERE conrelid = to_regclass('kimcloud.operations')
			AND conname = 'operations_result_version_fkey' AND convalidated
		) AND EXISTS (
			SELECT 1 FROM pg_trigger
			WHERE tgrelid = to_regclass('kimcloud.guitar_uploads')
			AND tgname = 'guitar_uploads_archive_withdrawn_score' AND tgenabled IN ('O','A')
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
