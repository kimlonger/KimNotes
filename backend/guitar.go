package main

import (
	"errors"
	"fmt"
	"net"
	"net/http"
	"strings"
	"time"
)

const guitarAppID = "guitar"

func (a *App) guitarHandler(fn func(http.ResponseWriter, *http.Request) error, private bool) http.HandlerFunc {
	wrap := func(w http.ResponseWriter, r *http.Request) error {
		if r.PathValue("app") != guitarAppID {
			return fail(404, "not_found", "内容不存在。")
		}
		return fn(w, r)
	}
	if private {
		return a.require(wrap)
	}
	return a.endpoint(wrap)
}
func (a *App) guitarRoutes(m *http.ServeMux, p string) {
	for _, route := range []struct {
		method, path string
		private      bool
		fn           func(http.ResponseWriter, *http.Request) error
	}{
		{"GET", "/session", false, a.guitarSession},
		{"GET", "/home", false, a.guitarHome}, {"GET", "/learning-stars", false, a.guitarLearningStars},
		{"GET", "/popular-scores", false, a.guitarPopularScores},
		{"GET", "/artists", false, a.guitarArtists},
		{"GET", "/artists/{artist}/avatar", false, a.guitarArtistAvatar},
		{"GET", "/scores", false, a.guitarScores}, {"GET", "/scores/{score}", true, a.guitarScore}, {"GET", "/scores/{score}/file", true, a.guitarScoreFile},
		{"GET", "/identity", true, a.guitarIdentity}, {"PUT", "/identity", true, a.guitarSetIdentity}, {"PUT", "/identity/avatar", true, a.guitarSetAvatar}, {"GET", "/avatars/{user}", false, a.guitarAvatar},
		{"PUT", "/identity/signature", true, a.guitarSetSignature},
		{"GET", "/scores/{score}/pages/{page}", true, a.guitarScorePage},
		{"GET", "/scores/{score}/download", true, a.guitarScoreDownload},
		{"GET", "/practice", true, a.guitarPracticeOverview}, {"POST", "/practice", true, a.guitarPracticeStart}, {"PUT", "/practice/{practice}", true, a.guitarPracticeStop},
		{"GET", "/workbench", true, a.guitarWorkbench}, {"PATCH", "/workbench", true, a.guitarUpdateWorkbench},
		{"PATCH", "/scores/{score}/reading", true, a.guitarUpdateReading},
		{"GET", "/score-requests", true, a.guitarScoreRequests}, {"POST", "/score-requests", true, a.guitarRequestScore},
		{"GET", "/score-requests/public", false, a.guitarPublicScoreRequests},
		{"GET", "/score-requests/{request}", false, a.guitarScoreRequest},
		{"PUT", "/score-requests/{request}/solution", true, a.guitarAcceptScoreAnswer},
		{"GET", "/community", false, a.guitarCommunityFeed},
		{"POST", "/community/questions", true, a.guitarAskCommunityQuestion},
		{"GET", "/community/questions/{question}", false, a.guitarCommunityQuestion},
		{"POST", "/community/questions/{question}/answers", true, a.guitarAnswerCommunityQuestion},
		{"GET", "/favorites", true, a.guitarFavorites}, {"PUT", "/favorites/{kind}/{target}", true, a.guitarFavorite}, {"DELETE", "/favorites/{kind}/{target}", true, a.guitarUnfavorite},
		{"GET", "/profile", true, a.guitarProfile}, {"POST", "/visits", true, a.guitarVisit},
		{"GET", "/uploads", true, a.guitarUploads}, {"POST", "/uploads", true, a.guitarUpload}, {"DELETE", "/uploads/{upload}", true, a.guitarRemoveUpload},
		{"GET", "/messages", false, a.guitarMessages}, {"POST", "/messages", false, a.guitarMessage},
	} {
		m.HandleFunc(route.method+" "+p+route.path, a.guitarHandler(route.fn, route.private))
	}
}

func (a *App) guitarSession(w http.ResponseWriter, r *http.Request) error {
	p, err := a.authenticate(r)
	if err != nil {
		var problem *apiError
		if errors.As(err, &problem) && problem.Status == 401 {
			writeJSON(w, 200, map[string]any{"user": nil})
			return nil
		}
		return err
	}
	member, err := a.guitarMember(r.Context(), p.UserID)
	if err != nil {
		return err
	}
	member["id"] = p.UserID
	member["email"] = p.Email
	member["emailVerified"] = p.Verified
	writeJSON(w, 200, map[string]any{"user": member})
	return nil
}

func (a *App) guitarFavorites(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	rows, e := a.db.Query(r.Context(), `SELECT f.kind,f.target_id,s.title,f.created_at,`+guitarCardColumns+`,
 (SELECT max(p.started_at) FROM guitar_practice_sessions p WHERE p.app_id=f.app_id AND p.user_id=f.user_id AND p.score_id=f.target_id AND p.ended_at IS NOT NULL AND p.seconds>0)
 FROM guitar_favorites f
 JOIN guitar_scores s ON f.kind='score' AND s.app_id=f.app_id AND s.id=f.target_id AND s.status='published'
 WHERE f.app_id=$1 AND f.user_id=$2
 ORDER BY f.created_at DESC,f.kind,f.target_id`, p.AppID, p.UserID)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var kind, id string
		var title *string
		var at time.Time
		var last *time.Time
		var card guitarCard
		if e = rows.Scan(append([]any{&kind, &id, &title, &at}, append(card.scanFields(), &last)...)...); e != nil {
			return e
		}
		row := map[string]any{"kind": kind, "id": id, "title": title, "createdAt": at, "lastPracticedAt": last}
		card.apply(row)
		out = append(out, row)
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"favorites": out})
	return nil
}
func (a *App) guitarFavorite(w http.ResponseWriter, r *http.Request) error {
	kind, id := r.PathValue("kind"), r.PathValue("target")
	if kind != "score" {
		return fail(400, "invalid_kind", "收藏类型无效。")
	}
	p := identity(r)
	var exists bool
	if e := a.db.QueryRow(r.Context(), `SELECT EXISTS(SELECT 1 FROM guitar_scores WHERE app_id=$1 AND id=$2 AND status='published')`, p.AppID, id).Scan(&exists); e != nil {
		return e
	}
	if !exists {
		return fail(404, "not_found", "内容不存在。")
	}
	_, e := a.db.Exec(r.Context(), `INSERT INTO guitar_favorites(app_id,user_id,kind,target_id) VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING`, p.AppID, p.UserID, kind, id)
	if e != nil {
		return e
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}
func (a *App) guitarUnfavorite(w http.ResponseWriter, r *http.Request) error {
	if r.PathValue("kind") != "score" {
		return fail(400, "invalid_kind", "收藏类型无效。")
	}
	p := identity(r)
	_, e := a.db.Exec(r.Context(), `DELETE FROM guitar_favorites WHERE app_id=$1 AND user_id=$2 AND kind=$3 AND target_id=$4`, p.AppID, p.UserID, r.PathValue("kind"), r.PathValue("target"))
	if e != nil {
		return e
	}
	writeJSON(w, 200, map[string]bool{"removed": true})
	return nil
}
func (a *App) guitarVisit(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	_, e := a.db.Exec(r.Context(), `INSERT INTO guitar_visits(app_id,user_id,visit_date) VALUES($1,$2,(now() AT TIME ZONE 'Asia/Shanghai')::date) ON CONFLICT DO NOTHING`, p.AppID, p.UserID)
	if e != nil {
		return e
	}
	return a.guitarProfile(w, r)
}
func (a *App) guitarProfile(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	var total, streak int
	e := a.db.QueryRow(r.Context(), `WITH days AS (SELECT visit_date,row_number() OVER(ORDER BY visit_date DESC)::integer AS n FROM guitar_visits WHERE app_id=$1 AND user_id=$2), anchor AS (SELECT max(visit_date) AS d FROM days) SELECT (SELECT count(*) FROM days),count(*) FILTER(WHERE visit_date=(SELECT d FROM anchor)-(n-1) AND (SELECT d FROM anchor)>=(now() AT TIME ZONE 'Asia/Shanghai')::date-1) FROM days`, p.AppID, p.UserID).Scan(&total, &streak)
	if e != nil {
		return e
	}
	title, next, remaining := guitarRank(total)
	writeJSON(w, 200, map[string]any{"totalDays": total, "streak": streak, "title": title, "nextTitle": next, "daysToNext": remaining, "progress": []any{}})
	return nil
}
func guitarRank(days int) (string, string, int) {
	levels := []struct {
		days  int
		title string
	}{{0, "吉他小白"}, {7, "入门琴友"}, {30, "吉他新秀"}, {60, "进阶琴手"}, {100, "吉他达人"}, {180, "吉他高手"}, {365, "吉他大师"}}
	for i := len(levels) - 1; i >= 0; i-- {
		if days >= levels[i].days {
			if i == len(levels)-1 {
				return levels[i].title, "", 0
			}
			return levels[i].title, levels[i+1].title, levels[i+1].days - days
		}
	}
	return "吉他小白", "入门琴友", 7
}
func guitarText(value string, min, max int) (string, error) {
	s := strings.TrimSpace(value)
	if n := len([]rune(s)); n < min || n > max {
		return "", fail(400, "invalid_text", fmt.Sprintf("内容长度需要 %d–%d 字。", min, max))
	}
	return s, nil
}
func (a *App) guitarWriteLimit(r *http.Request, scope string, limit int) error {
	key := r.Header.Get("X-Kim-Client-IP")
	if key == "" {
		key, _, _ = net.SplitHostPort(r.RemoteAddr)
	}
	return a.limited(r, "guitar:"+scope+":"+key, limit, time.Hour)
}
