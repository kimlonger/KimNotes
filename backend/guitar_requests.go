package main

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

const guitarVisibleRequest = `m.app_id='guitar' AND m.requested_title IS NOT NULL AND m.status<>'hidden'`

const guitarRequestNote = `CASE WHEN strpos(m.body,chr(10))>0 THEN substring(m.body FROM strpos(m.body,chr(10))+1) ELSE '' END`
const guitarRequestAnswersJoin = ` LEFT JOIN LATERAL (
 SELECT count(*) AS count,max(a.score_id) FILTER (WHERE a.accepted_at IS NOT NULL) AS solution
 FROM guitar_request_answers a JOIN guitar_scores s ON s.app_id=a.app_id AND s.id=a.score_id AND s.status='published'
 WHERE a.app_id=m.app_id AND a.request_id=m.id
) answers ON true `

const guitarRequestSelect = `
 SELECT m.id::text,m.requested_title,COALESCE(m.requested_artist,''),` + guitarRequestNote + `,
 COALESCE(g.nickname,m.nickname),m.created_at,answers.count,COALESCE(answers.solution,''),
 COALESCE(m.author_id=$1::uuid,false)
 FROM guitar_messages m
 LEFT JOIN guitar_members g ON g.app_id=m.app_id AND g.user_id=m.author_id` +
	guitarRequestAnswersJoin + ` WHERE ` + guitarVisibleRequest

type guitarRequest struct {
	ID              string    `json:"id"`
	Title           string    `json:"title"`
	Artist          string    `json:"artist"`
	Note            string    `json:"note"`
	Nickname        string    `json:"nickname"`
	CreatedAt       time.Time `json:"createdAt"`
	AnswerCount     int       `json:"answerCount"`
	SolutionScoreID string    `json:"solutionScoreId"`
	CanResolve      bool      `json:"canResolve"`
	Status          string    `json:"status"`
}

func scanGuitarRequest(row pgx.Row) (guitarRequest, error) {
	var out guitarRequest
	err := row.Scan(&out.ID, &out.Title, &out.Artist, &out.Note, &out.Nickname,
		&out.CreatedAt, &out.AnswerCount, &out.SolutionScoreID, &out.CanResolve)
	out.Status = "open"
	if out.SolutionScoreID != "" {
		out.Status = "solved"
	}
	return out, err
}

func (a *App) guitarScoreRequests(w http.ResponseWriter, r *http.Request) error {
	return a.guitarListScoreRequests(w, r, identity(r).UserID)
}

func (a *App) guitarPublicScoreRequests(w http.ResponseWriter, r *http.Request) error {
	return a.guitarListScoreRequests(w, r, nil)
}

func (a *App) guitarListScoreRequests(w http.ResponseWriter, r *http.Request, owner any) error {
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	size, err := guitarCatalogNumber(r, "pageSize", 12, 50)
	if err != nil {
		return err
	}
	const mine = ` AND ($1::uuid IS NULL OR m.author_id=$1)`
	var total int
	if err := a.db.QueryRow(r.Context(), `SELECT count(*) FROM guitar_messages m WHERE `+guitarVisibleRequest+mine, owner).Scan(&total); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), guitarRequestSelect+mine+
		` ORDER BY (answers.solution IS NULL) DESC,m.created_at DESC,m.id LIMIT $2 OFFSET $3`, owner, size, (page-1)*size)
	if err != nil {
		return err
	}
	defer rows.Close()
	requests := []guitarRequest{}
	for rows.Next() {
		item, err := scanGuitarRequest(rows)
		if err != nil {
			return err
		}
		requests = append(requests, item)
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"requests": requests, "pagination": guitarHomePagination(page, size, total)})
	return nil
}

func (a *App) guitarScoreRequest(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("request")
	if !validID(id) {
		return fail(404, "not_found", "求谱请求不存在。")
	}
	var viewer any
	if account, err := a.authenticate(r); err == nil {
		if expected := r.Header.Get("X-Kim-Expected-User"); expected != "" && expected != account.UserID {
			return fail(409, "account_changed", "账号已切换，请刷新后重试。")
		}
		viewer = account.UserID
	} else if problem, ok := err.(*apiError); !ok || problem.Status != 401 {
		return err
	}
	request, err := scanGuitarRequest(a.db.QueryRow(r.Context(), guitarRequestSelect+` AND m.id=$2`, viewer, id))
	if err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT s.id,s.title,s.artist,COALESCE(g.nickname,'琴友'),
 a.created_at,(a.accepted_at IS NOT NULL)
 FROM guitar_request_answers a JOIN guitar_scores s ON s.app_id=a.app_id AND s.id=a.score_id AND s.status='published'
 LEFT JOIN guitar_members g ON g.app_id=a.app_id AND g.user_id=a.responder_id
 WHERE a.app_id='guitar' AND a.request_id=$1
 ORDER BY (a.accepted_at IS NOT NULL) DESC,a.created_at,a.score_id`, id)
	if err != nil {
		return err
	}
	defer rows.Close()
	answers := []map[string]any{}
	for rows.Next() {
		var score, title, artist, nickname string
		var created time.Time
		var accepted bool
		if err := rows.Scan(&score, &title, &artist, &nickname, &created, &accepted); err != nil {
			return err
		}
		answers = append(answers, map[string]any{"scoreId": score, "title": title, "artist": artist, "nickname": nickname, "createdAt": created, "accepted": accepted})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"request": request, "answers": answers})
	return nil
}

// Serialize submissions and resolutions of a request so concurrent answers cannot close it twice.
func lockGuitarRequest(ctx context.Context, tx pgx.Tx, id string) (*string, error) {
	if !validID(id) {
		return nil, fail(404, "not_found", "求谱请求不存在。")
	}
	var owner *string
	err := tx.QueryRow(ctx, `SELECT m.author_id::text FROM guitar_messages m WHERE `+guitarVisibleRequest+` AND m.id=$1 FOR UPDATE`, id).Scan(&owner)
	return owner, err
}

func guitarRequestSolution(ctx context.Context, tx pgx.Tx, id string) (string, error) {
	var score string
	err := tx.QueryRow(ctx, `SELECT a.score_id FROM guitar_request_answers a JOIN guitar_scores s
 ON s.app_id=a.app_id AND s.id=a.score_id AND s.status='published'
 WHERE a.app_id='guitar' AND a.request_id=$1 AND a.accepted_at IS NOT NULL`, id).Scan(&score)
	if errors.Is(err, pgx.ErrNoRows) {
		return "", nil
	}
	return score, err
}

func (a *App) guitarAcceptScoreAnswer(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		ScoreID string `json:"scoreId"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	in.ScoreID = strings.TrimSpace(in.ScoreID)
	if in.ScoreID == "" || len(in.ScoreID) > 256 {
		return fail(400, "invalid_score", "请选择站内公开曲谱。")
	}
	if err := a.guitarWriteLimit(r, "score-answer", 40); err != nil {
		return err
	}
	id, ctx, user := r.PathValue("request"), r.Context(), identity(r).UserID
	err := pgx.BeginFunc(ctx, a.db, func(tx pgx.Tx) error {
		owner, err := lockGuitarRequest(ctx, tx, id)
		if err != nil {
			return err
		}
		if owner == nil || *owner != user {
			return fail(403, "not_request_owner", "只有发起人可以确认解决。")
		}
		solution, err := guitarRequestSolution(ctx, tx, id)
		if err != nil {
			return err
		}
		if solution == in.ScoreID {
			return nil
		}
		if solution != "" {
			return fail(409, "request_solved", "这条求谱已解决。")
		}
		var score string
		if err := tx.QueryRow(ctx, `SELECT id FROM guitar_scores WHERE app_id='guitar' AND id=$1 AND status='published' FOR SHARE`, in.ScoreID).Scan(&score); err != nil {
			return err
		}
		// A previously adopted score may have been taken down. Clear it in the same transaction.
		if _, err := tx.Exec(ctx, `UPDATE guitar_request_answers SET accepted_at=NULL WHERE app_id='guitar' AND request_id=$1 AND accepted_at IS NOT NULL`, id); err != nil {
			return err
		}
		tag, err := tx.Exec(ctx, `UPDATE guitar_request_answers SET accepted_at=now() WHERE app_id='guitar' AND request_id=$1 AND score_id=$2`, id, score)
		if err == nil && tag.RowsAffected() == 0 {
			return fail(404, "not_found", "这份曲谱还没有被提交为回应。")
		}
		return err
	})
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}

func (a *App) guitarRequestScore(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Title  string `json:"title"`
		Artist string `json:"artist"`
		Note   string `json:"note"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	title, err := guitarText(in.Title, 1, 100)
	if err != nil {
		return err
	}
	artist, err := guitarText(in.Artist, 0, 120)
	if err != nil {
		return err
	}
	note, err := guitarText(in.Note, 0, 500)
	if err != nil {
		return err
	}
	if err := a.guitarWriteLimit(r, "message", 12); err != nil {
		return err
	}
	user := identity(r).UserID
	member, err := a.guitarMember(r.Context(), user)
	if err != nil {
		return err
	}
	body := strings.TrimSpace("求谱：《" + title + "》 " + artist + "\n" + note)
	id := newID()
	_, err = a.db.Exec(r.Context(), `INSERT INTO guitar_messages(id,nickname,body,author_id,requested_title,requested_artist)
 VALUES($1,$2,$3,$4,$5,$6)`, id, member["nickname"], body, user, title, artist)
	if err != nil {
		return err
	}
	writeJSON(w, 201, map[string]any{"saved": true, "id": id})
	return nil
}
