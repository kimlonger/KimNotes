package main

import (
	"context"
	"errors"
	"testing"

	"github.com/jackc/pgx/v5/pgconn"
)

func TestGuitarMediaIntegrityConstraints(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	if _, err := e.db.Exec(ctx, guitarMediaIntegritySchema); err != nil {
		t.Fatal(err)
	}
	guitarTestAccount(t, e, "media-integrity")
	var userID string
	if err := e.db.QueryRow(ctx, "SELECT id FROM users WHERE email='media-integrity@example.com'").Scan(&userID); err != nil {
		t.Fatal(err)
	}
	const updateAvatar = "UPDATE guitar_members SET avatar_bytes=$2,avatar_mime=$3 WHERE app_id='guitar' AND user_id=$1"
	const insertScore = `INSERT INTO guitar_scores(id,title,category,difficulty,credit,notation,bytes,mime)
 VALUES($1,'Media integrity','练习','入门','test',$2,$3,$4)`
	for _, tc := range []struct {
		name       string
		query      string
		args       []any
		constraint string
	}{
		{"avatar preset", updateAvatar, []any{userID, nil, nil}, ""},
		{"avatar custom", updateAvatar, []any{userID, []byte{1}, "image/png"}, ""},
		{"avatar no mime", updateAvatar, []any{userID, []byte{1}, nil}, "guitar_members_avatar_media_valid"},
		{"avatar no bytes", updateAvatar, []any{userID, nil, "image/png"}, "guitar_members_avatar_media_valid"},
		{"avatar empty", updateAvatar, []any{userID, []byte{}, "image/png"}, "guitar_members_avatar_media_valid"},
		{"score notation", insertScore, []any{"notation-score", "{}", nil, nil}, ""},
		{"score pdf", insertScore, []any{"pdf-score", nil, []byte{1}, "application/pdf"}, ""},
		{"score image", insertScore, []any{"image-score", nil, []byte{1}, "image/webp"}, ""},
		{"score no mime", insertScore, []any{"no-mime-score", nil, []byte{1}, nil}, "guitar_scores_file_media_valid"},
		{"score mime without file", insertScore, []any{"no-file-score", "{}", nil, "image/png"}, "guitar_scores_file_media_valid"},
		{"score invalid mime", insertScore, []any{"invalid-mime-score", nil, []byte{1}, "text/plain"}, "guitar_scores_file_media_valid"},
		{"score empty file", insertScore, []any{"empty-score", nil, []byte{}, "application/pdf"}, "guitar_scores_file_media_valid"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			tag, err := e.db.Exec(ctx, tc.query, tc.args...)
			if tc.constraint == "" {
				if err != nil || tag.RowsAffected() != 1 {
					t.Fatalf("valid media pair was not saved: %v, %v", tag, err)
				}
				return
			}
			var pgErr *pgconn.PgError
			if !errors.As(err, &pgErr) || pgErr.Code != "23514" || pgErr.ConstraintName != tc.constraint {
				t.Fatalf("expected check violation %s, got %v", tc.constraint, err)
			}
		})
	}
}

func TestCleanupExpiredAuthChallenges(t *testing.T) {
	e := newTestEnv(t)
	ctx := context.Background()
	expiredRegister, expiredReset, activeRegister, activeReset := newID(), newID(), newID(), newID()
	e.sql(`INSERT INTO auth_challenges(id,app_id,email,purpose,token_hash,expires_at) VALUES
 ($1,'kimnotes','expired-register@example.com','register','expired',now()-interval '1 second'),
 ($2,'main','expired-reset@example.com','reset','expired',now()-interval '1 second'),
 ($3,'kimnotes','active-register@example.com','register','active',now()+interval '10 minutes'),
 ($4,'main','active-reset@example.com','reset','active',now()+interval '30 minutes')`,
		expiredRegister, expiredReset, activeRegister, activeReset)
	if err := e.app.cleanupSessionCaches(ctx); err != nil {
		t.Fatal(err)
	}
	var expired, active int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FILTER(WHERE id IN ($1,$2)),count(*) FILTER(WHERE id IN ($3,$4))
 FROM auth_challenges`, expiredRegister, expiredReset, activeRegister, activeReset).Scan(&expired, &active); err != nil {
		t.Fatal(err)
	}
	if expired != 0 || active != 2 {
		t.Fatalf("expected only expired challenges to be deleted, got expired=%d active=%d", expired, active)
	}
}
