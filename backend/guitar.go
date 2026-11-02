package main

import (
	"encoding/json"
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
		{"GET", "/courses", false, a.guitarCourses}, {"GET", "/courses/{course}", false, a.guitarCourse},
		{"GET", "/lessons/{lesson}", true, a.guitarLesson}, {"PUT", "/lessons/{lesson}/progress", true, a.guitarProgress},
		{"POST", "/lessons/{lesson}/open", true, a.guitarOpen},
		{"GET", "/scores", true, a.guitarScores}, {"GET", "/scores/{score}", true, a.guitarScore}, {"GET", "/scores/{score}/file", true, a.guitarScoreFile},
		{"GET", "/favorites", true, a.guitarFavorites}, {"PUT", "/favorites/{kind}/{target}", true, a.guitarFavorite}, {"DELETE", "/favorites/{kind}/{target}", true, a.guitarUnfavorite},
		{"GET", "/profile", true, a.guitarProfile}, {"POST", "/visits", true, a.guitarVisit},
		{"GET", "/uploads", true, a.guitarUploads}, {"POST", "/uploads", true, a.guitarUpload}, {"DELETE", "/uploads/{upload}", true, a.guitarRemoveUpload},
		{"GET", "/messages", false, a.guitarMessages}, {"POST", "/messages", false, a.guitarMessage},
		{"GET", "/courses/{course}/questions", false, a.guitarQuestions}, {"POST", "/courses/{course}/questions", true, a.guitarQuestion}, {"POST", "/questions/{question}/replies", true, a.guitarReply},
	} {
		m.HandleFunc(route.method+" "+p+route.path, a.guitarHandler(route.fn, route.private))
	}
}

type guitarCourse struct {
	ID          string `json:"id"`
	Title       string `json:"title"`
	Summary     string `json:"summary"`
	Level       string `json:"level"`
	Status      string `json:"status"`
	LessonCount int    `json:"lessonCount"`
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
	writeJSON(w, 200, map[string]any{"user": map[string]any{"id": p.UserID, "email": p.Email, "emailVerified": p.Verified}})
	return nil
}

type guitarChapter struct {
	ID      string                `json:"id"`
	Title   string                `json:"title"`
	Lessons []guitarLessonSummary `json:"lessons"`
}
type guitarLessonSummary struct {
	ID    string `json:"id"`
	Title string `json:"title"`
}

func (a *App) guitarCourses(w http.ResponseWriter, r *http.Request) error {
	rows, e := a.db.Query(r.Context(), `SELECT c.id,c.title,c.summary,c.level,c.status,count(l.id) FROM guitar_courses c LEFT JOIN guitar_chapters ch ON ch.app_id=c.app_id AND ch.course_id=c.id LEFT JOIN guitar_lessons l ON l.app_id=ch.app_id AND l.chapter_id=ch.id WHERE c.app_id=$1 AND c.status IN ('published','planned') GROUP BY c.app_id,c.id ORDER BY c.sort_order,c.id`, guitarAppID)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []guitarCourse{}
	for rows.Next() {
		var c guitarCourse
		if e = rows.Scan(&c.ID, &c.Title, &c.Summary, &c.Level, &c.Status, &c.LessonCount); e != nil {
			return e
		}
		out = append(out, c)
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"courses": out})
	return nil
}
func (a *App) guitarCourse(w http.ResponseWriter, r *http.Request) error {
	var c guitarCourse
	e := a.db.QueryRow(r.Context(), `SELECT id,title,summary,level,status FROM guitar_courses WHERE app_id=$1 AND id=$2 AND status IN ('published','planned')`, guitarAppID, r.PathValue("course")).Scan(&c.ID, &c.Title, &c.Summary, &c.Level, &c.Status)
	if e != nil {
		return e
	}
	rows, e := a.db.Query(r.Context(), `SELECT ch.id,ch.title,l.id,l.title FROM guitar_chapters ch JOIN guitar_lessons l ON l.app_id=ch.app_id AND l.chapter_id=ch.id WHERE ch.app_id=$1 AND ch.course_id=$2 ORDER BY ch.sort_order,l.sort_order`, guitarAppID, c.ID)
	if e != nil {
		return e
	}
	defer rows.Close()
	chapters := []guitarChapter{}
	for rows.Next() {
		var id, title, lid, lt string
		if e = rows.Scan(&id, &title, &lid, &lt); e != nil {
			return e
		}
		if len(chapters) == 0 || chapters[len(chapters)-1].ID != id {
			chapters = append(chapters, guitarChapter{id, title, []guitarLessonSummary{}})
		}
		i := len(chapters) - 1
		chapters[i].Lessons = append(chapters[i].Lessons, guitarLessonSummary{lid, lt})
		c.LessonCount++
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"course": c, "chapters": chapters})
	return nil
}
func (a *App) guitarLesson(w http.ResponseWriter, r *http.Request) error {
	var id, title, cid, chid, cht string
	var content json.RawMessage
	e := a.db.QueryRow(r.Context(), `SELECT l.id,l.title,l.content,c.id,ch.id,ch.title FROM guitar_lessons l JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id JOIN guitar_courses c ON c.app_id=ch.app_id AND c.id=ch.course_id WHERE l.app_id=$1 AND l.id=$2 AND c.status='published'`, guitarAppID, r.PathValue("lesson")).Scan(&id, &title, &content, &cid, &chid, &cht)
	if e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"id": id, "title": title, "courseId": cid, "chapterId": chid, "chapterTitle": cht, "content": content})
	return nil
}
func (a *App) guitarOpen(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	tag, err := a.db.Exec(r.Context(), `INSERT INTO guitar_progress(app_id,user_id,lesson_id) SELECT $1,$2,l.id FROM guitar_lessons l JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id JOIN guitar_courses c ON c.app_id=ch.app_id AND c.id=ch.course_id WHERE l.app_id=$1 AND l.id=$3 AND c.status='published' ON CONFLICT(app_id,user_id,lesson_id) DO UPDATE SET updated_at=now()`, p.AppID, p.UserID, r.PathValue("lesson"))
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "课时不存在。")
	}
	writeJSON(w, 200, map[string]bool{"recorded": true})
	return nil
}
func (a *App) guitarProgress(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Completed bool     `json:"completed"`
		Checks    []string `json:"checks"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	if len(in.Checks) > 16 {
		return fail(400, "invalid_checks", "自检项目过多。")
	}
	for _, s := range in.Checks {
		if len(s) > 80 {
			return fail(400, "invalid_checks", "自检项目无效。")
		}
	}
	p := identity(r)
	checks, _ := json.Marshal(in.Checks)
	if in.Checks == nil {
		checks = []byte("[]")
	}
	tag, e := a.db.Exec(r.Context(), `INSERT INTO guitar_progress(app_id,user_id,lesson_id,completed,checks) SELECT $1,$2,l.id,$4,$5 FROM guitar_lessons l JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id JOIN guitar_courses c ON c.app_id=ch.app_id AND c.id=ch.course_id WHERE l.app_id=$1 AND l.id=$3 AND c.status='published' ON CONFLICT(app_id,user_id,lesson_id) DO UPDATE SET completed=EXCLUDED.completed,checks=EXCLUDED.checks,updated_at=now()`, p.AppID, p.UserID, r.PathValue("lesson"), in.Completed, checks)
	if e != nil {
		return e
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "课时不存在。")
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}
func (a *App) guitarFavorites(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	rows, e := a.db.Query(r.Context(), `SELECT f.kind,f.target_id,COALESCE(c.title,l.title,s.title),f.created_at FROM guitar_favorites f LEFT JOIN guitar_courses c ON f.kind='course' AND c.app_id=f.app_id AND c.id=f.target_id AND c.status='published' LEFT JOIN guitar_lessons l ON f.kind='lesson' AND l.app_id=f.app_id AND l.id=f.target_id LEFT JOIN guitar_scores s ON f.kind='score' AND s.app_id=f.app_id AND s.id=f.target_id AND s.status='published' WHERE f.app_id=$1 AND f.user_id=$2 ORDER BY f.created_at DESC LIMIT 500`, p.AppID, p.UserID)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var kind, id string
		var title *string
		var at time.Time
		if e = rows.Scan(&kind, &id, &title, &at); e != nil {
			return e
		}
		out = append(out, map[string]any{"kind": kind, "id": id, "title": title, "createdAt": at})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"favorites": out})
	return nil
}
func (a *App) guitarFavorite(w http.ResponseWriter, r *http.Request) error {
	kind, id := r.PathValue("kind"), r.PathValue("target")
	q := ""
	switch kind {
	case "course":
		q = `SELECT EXISTS(SELECT 1 FROM guitar_courses WHERE app_id=$1 AND id=$2 AND status='published')`
	case "lesson":
		q = `SELECT EXISTS(SELECT 1 FROM guitar_lessons l JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id JOIN guitar_courses c ON c.app_id=ch.app_id AND c.id=ch.course_id WHERE l.app_id=$1 AND l.id=$2 AND c.status='published')`
	case "score":
		q = `SELECT EXISTS(SELECT 1 FROM guitar_scores WHERE app_id=$1 AND id=$2 AND status='published')`
	default:
		return fail(400, "invalid_kind", "收藏类型无效。")
	}
	p := identity(r)
	var exists bool
	if e := a.db.QueryRow(r.Context(), q, p.AppID, id).Scan(&exists); e != nil {
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
	rows, e := a.db.Query(r.Context(), `SELECT p.lesson_id,l.title,ch.course_id,p.completed,p.checks,p.updated_at FROM guitar_progress p JOIN guitar_lessons l ON l.app_id=p.app_id AND l.id=p.lesson_id JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id JOIN guitar_courses c ON c.app_id=ch.app_id AND c.id=ch.course_id WHERE p.app_id=$1 AND p.user_id=$2 AND c.status='published' ORDER BY p.updated_at DESC LIMIT 500`, p.AppID, p.UserID)
	if e != nil {
		return e
	}
	defer rows.Close()
	progress := []map[string]any{}
	for rows.Next() {
		var id, lt, cid string
		var done bool
		var checks json.RawMessage
		var at time.Time
		if e = rows.Scan(&id, &lt, &cid, &done, &checks, &at); e != nil {
			return e
		}
		progress = append(progress, map[string]any{"lessonId": id, "title": lt, "courseId": cid, "completed": done, "checks": checks, "updatedAt": at})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"totalDays": total, "streak": streak, "title": title, "nextTitle": next, "daysToNext": remaining, "progress": progress})
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
