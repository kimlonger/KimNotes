// cleanup-e2e removes only synthetic accounts listed in a live-api test journal.
// It is an operator tool, excluded from the deployed HTTP service image.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"os"
	"strings"
	"time"
)

func main() {
	if e := run(); e != nil {
		fmt.Fprintln(os.Stderr, e)
		os.Exit(1)
	}
}
func run() error {
	journalPath := flag.String("journal", "", "exact journal produced by website/tests/live-api.mjs")
	flag.Parse()
	if *journalPath == "" {
		return fmt.Errorf("journal required")
	}
	raw, e := os.ReadFile(*journalPath)
	if e != nil {
		return e
	}
	var j struct {
		Origin    string    `json:"origin"`
		CreatedAt time.Time `json:"createdAt"`
		Accounts  []struct {
			ID    string `json:"id"`
			Email string `json:"email"`
		} `json:"accounts"`
	}
	if e = json.Unmarshal(raw, &j); e != nil {
		return e
	}
	if j.CreatedAt.IsZero() || time.Since(j.CreatedAt) > 24*time.Hour || len(j.Accounts) == 0 || len(j.Accounts) > 10 {
		return fmt.Errorf("invalid or expired test journal")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	db, e := pgxpool.New(ctx, os.Getenv("DATABASE_URL"))
	if e != nil {
		return e
	}
	defer db.Close()
	tx, e := db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	for _, account := range j.Accounts {
		if !strings.HasPrefix(account.Email, "kimnotes-e2e-") || !strings.HasSuffix(account.Email, "@example.com") || len(account.ID) != 36 {
			return fmt.Errorf("refusing non-fixture account")
		}
		var email string
		var created time.Time
		e = tx.QueryRow(ctx, "SELECT email,created_at FROM kimcloud.users WHERE id=$1 FOR UPDATE", account.ID).Scan(&email, &created)
		if e == pgx.ErrNoRows {
			continue
		}
		if e != nil {
			return e
		}
		if email != account.Email || created.Before(j.CreatedAt.Add(-time.Minute)) || created.After(j.CreatedAt.Add(2*time.Hour)) {
			return fmt.Errorf("fixture ownership check failed")
		}
		for _, table := range []string{"operations", "note_changes", "note_versions", "notes", "attachments", "sessions", "user_apps"} {
			if _, e = tx.Exec(ctx, "DELETE FROM kimcloud."+table+" WHERE user_id=$1", account.ID); e != nil {
				return e
			}
		}
		if _, e = tx.Exec(ctx, "DELETE FROM kimcloud.auth_challenges WHERE email=$1", email); e != nil {
			return e
		}
		if _, e = tx.Exec(ctx, "DELETE FROM kimcloud.users WHERE id=$1 AND email=$2", account.ID, email); e != nil {
			return e
		}
	}
	if e = tx.Commit(ctx); e != nil {
		return e
	}
	fmt.Printf("Cleaned %d owned synthetic test accounts.\n", len(j.Accounts))
	return nil
}
