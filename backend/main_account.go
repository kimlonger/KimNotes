package main

import (
	"context"
	"errors"
	"net/http"
	"time"

	"github.com/jackc/pgx/v5"
)

const retainedAccountEntries = 100

type accountFollows struct {
	ProjectIDs []string   `json:"projectIds"`
	UpdatedAt  *time.Time `json:"updatedAt"`
}

type accountEntry struct {
	ProjectID string    `json:"projectId"`
	Action    string    `json:"action"`
	CreatedAt time.Time `json:"createdAt"`
}

type accountReader interface {
	QueryRow(context.Context, string, ...any) pgx.Row
}

// These endpoints are main-site capabilities. A valid cookie from another app
// cannot opt into them, even though every app shares the same users table.
func (a *App) mainAccount(next func(http.ResponseWriter, *http.Request) error) func(http.ResponseWriter, *http.Request) error {
	return func(w http.ResponseWriter, r *http.Request) error {
		p := identity(r)
		if p.AppID != "main" {
			return fail(404, "not_found", "此应用不提供该账户功能。")
		}
		if r.Header.Get("X-Kim-Expected-User") != p.UserID {
			return fail(409, "account_changed", "请确认当前账号后重试。")
		}
		if r.Method == http.MethodGet {
			if e := a.limited(r, "main:read:"+p.UserID, 90, time.Minute); e != nil {
				return e
			}
		}
		return next(w, r)
	}
}

func (a *App) validateMainProjects(ctx context.Context, ids []string) error {
	for _, id := range ids {
		if !projectIDPattern.MatchString(id) {
			return fail(400, "invalid_project_ids", "作品 ID 无效。")
		}
		if len(a.config.MainProjectIDs) > 0 {
			allowed := false
			for _, limit := range a.config.MainProjectIDs {
				allowed = allowed || id == limit
			}
			if !allowed {
				return fail(400, "invalid_project_ids", "作品暂不可用。")
			}
		}
	}
	if len(ids) == 0 {
		return nil
	}
	var count int
	if err := a.db.QueryRow(ctx, "SELECT count(*) FROM content_works WHERE status='published' AND id=ANY($1)", ids).Scan(&count); err != nil {
		return err
	}
	if count != len(ids) {
		return fail(400, "invalid_project_ids", "作品尚未发布或已不可用。")
	}
	return nil
}

func readAccountFollows(ctx context.Context, db accountReader, p principal) (accountFollows, error) {
	out := accountFollows{ProjectIDs: []string{}}
	err := db.QueryRow(ctx, "SELECT project_ids,updated_at FROM main_account_follows WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID).Scan(&out.ProjectIDs, &out.UpdatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return out, nil
	}
	return out, err
}

func (a *App) getFollows(w http.ResponseWriter, r *http.Request) error {
	out, err := readAccountFollows(r.Context(), a.db, identity(r))
	if err != nil {
		return err
	}
	writeJSON(w, 200, out)
	return nil
}

func (a *App) putFollows(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		ProjectIDs []string `json:"projectIds"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if in.ProjectIDs == nil || len(in.ProjectIDs) > 64 {
		return fail(400, "invalid_project_ids", "请提供作品 ID 数组，最多 64 件。")
	}
	seen := map[string]bool{}
	for _, id := range in.ProjectIDs {
		if seen[id] {
			return fail(400, "invalid_project_ids", "作品 ID 无效或重复。")
		}
		seen[id] = true
	}
	if err := a.validateMainProjects(r.Context(), in.ProjectIDs); err != nil {
		return err
	}
	p := identity(r)
	if e := a.limited(r, "main:follows:"+p.UserID, 30, time.Minute); e != nil {
		return e
	}
	out := accountFollows{ProjectIDs: in.ProjectIDs}
	err := a.db.QueryRow(r.Context(), `INSERT INTO main_account_follows(app_id,user_id,project_ids)
VALUES($1,$2,$3) ON CONFLICT(app_id,user_id) DO UPDATE SET
project_ids=EXCLUDED.project_ids,updated_at=CASE WHEN main_account_follows.project_ids IS DISTINCT FROM EXCLUDED.project_ids THEN clock_timestamp() ELSE main_account_follows.updated_at END
RETURNING updated_at`, p.AppID, p.UserID, in.ProjectIDs).Scan(&out.UpdatedAt)
	if err != nil {
		return err
	}
	writeJSON(w, 200, out)
	return nil
}

func validEntryAction(action string) bool {
	return action == "open" || action == "download" || action == "source" || action == "details"
}

func (a *App) recordEntry(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		ProjectID string `json:"projectId"`
		Action    string `json:"action"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if !validEntryAction(in.Action) {
		return fail(400, "invalid_entry", "作品或入口类型无效。")
	}
	if err := a.validateMainProjects(r.Context(), []string{in.ProjectID}); err != nil {
		return err
	}
	p := identity(r)
	if e := a.limited(r, "main:entries:"+p.UserID, 120, time.Hour); e != nil {
		return e
	}
	tx, err := a.db.Begin(r.Context())
	if err != nil {
		return err
	}
	defer tx.Rollback(r.Context())
	// A per-account lock makes concurrent increments and trimming atomic. It
	// does not lock the user's kimnotes row or change any note sync cursor.
	var cursor int64
	if err = tx.QueryRow(r.Context(), "SELECT cursor FROM user_apps WHERE app_id=$1 AND user_id=$2 FOR UPDATE", p.AppID, p.UserID).Scan(&cursor); err != nil {
		return err
	}
	if _, err = tx.Exec(r.Context(), `INSERT INTO main_account_totals(app_id,user_id,project_id,entry_count) VALUES($1,$2,$3,1)
ON CONFLICT(app_id,user_id,project_id) DO UPDATE SET entry_count=main_account_totals.entry_count+1`, p.AppID, p.UserID, in.ProjectID); err != nil {
		return err
	}
	out := accountEntry{ProjectID: in.ProjectID, Action: in.Action}
	if err = tx.QueryRow(r.Context(), "INSERT INTO main_account_entries(app_id,user_id,project_id,action) VALUES($1,$2,$3,$4) RETURNING created_at", p.AppID, p.UserID, in.ProjectID, in.Action).Scan(&out.CreatedAt); err != nil {
		return err
	}
	if _, err = tx.Exec(r.Context(), `DELETE FROM main_account_entries WHERE app_id=$1 AND user_id=$2 AND id NOT IN
(SELECT id FROM main_account_entries WHERE app_id=$1 AND user_id=$2 ORDER BY id DESC LIMIT $3)`, p.AppID, p.UserID, retainedAccountEntries); err != nil {
		return err
	}
	if err = tx.Commit(r.Context()); err != nil {
		return err
	}
	writeJSON(w, 201, map[string]any{"recorded": true, "entry": out})
	return nil
}

func (a *App) accountOverview(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	tx, err := a.db.BeginTx(r.Context(), pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if err != nil {
		return err
	}
	defer tx.Rollback(r.Context())
	follows, err := readAccountFollows(r.Context(), tx, p)
	if err != nil {
		return err
	}
	rows, err := tx.Query(r.Context(), "SELECT project_id,entry_count FROM main_account_totals WHERE app_id=$1 AND user_id=$2", p.AppID, p.UserID)
	if err != nil {
		return err
	}
	byProject := map[string]int64{}
	var total int64
	for rows.Next() {
		var id string
		var count int64
		if err = rows.Scan(&id, &count); err != nil {
			rows.Close()
			return err
		}
		byProject[id], total = count, total+count
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	rows, err = tx.Query(r.Context(), "SELECT project_id,action,created_at FROM main_account_entries WHERE app_id=$1 AND user_id=$2 ORDER BY id DESC LIMIT 20", p.AppID, p.UserID)
	if err != nil {
		return err
	}
	recent := []accountEntry{}
	for rows.Next() {
		var entry accountEntry
		if err = rows.Scan(&entry.ProjectID, &entry.Action, &entry.CreatedAt); err != nil {
			rows.Close()
			return err
		}
		recent = append(recent, entry)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	if err = tx.Commit(r.Context()); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{
		"follows":  map[string]any{"count": len(follows.ProjectIDs), "projectIds": follows.ProjectIDs, "updatedAt": follows.UpdatedAt},
		"activity": map[string]any{"total": total, "byProject": byProject, "recent": recent, "kind": "site_entries"},
	})
	return nil
}

func (a *App) notesSummary(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	var count, images int64
	var exact bool
	var last *time.Time
	// The only cross-app read is a same-user aggregate. No document, title,
	// attachment reference or private URL is selected or returned by this API.
	// Old compressed RTF without image references cannot be classified exactly
	// without decoding private bodies, so explicitly mark that case inexact.
	err := a.db.QueryRow(r.Context(), `SELECT count(*),count(*) FILTER (WHERE
COALESCE(jsonb_array_length(NULLIF(document->'attachments','null'::jsonb)),0)>0 OR
COALESCE(jsonb_array_length(NULLIF(document->'rtfImages','null'::jsonb)),0)>0 OR
strpos(COALESCE(document->>'rtf',''), '\pict')>0),
NOT COALESCE(bool_or(COALESCE(document->>'rtfGzip','')<>'' AND COALESCE(jsonb_array_length(NULLIF(document->'rtfImages','null'::jsonb)),0)=0 AND COALESCE(jsonb_array_length(NULLIF(document->'attachments','null'::jsonb)),0)=0),false),max(updated_at)
FROM notes WHERE app_id='kimnotes' AND user_id=$1 AND deleted_at IS NULL`, p.UserID).Scan(&count, &images, &exact, &last)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"noteCount": count, "imageNoteCount": images, "countIsExact": exact, "lastUpdatedAt": last, "checkedAt": time.Now().UTC(), "website": "https://note.kimlulu.com/notes/"})
	return nil
}
