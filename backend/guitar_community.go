package main

import (
	"encoding/json"
	"net/http"
	"time"
)

func (a *App) guitarMessages(w http.ResponseWriter, r *http.Request) error {
	rows, e := a.db.Query(r.Context(), `SELECT id::text,nickname,body,status,reply,created_at FROM guitar_messages WHERE app_id=$1 AND status<>'hidden' ORDER BY created_at DESC,id LIMIT 100`, guitarAppID)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, nick, body, status, reply string
		var at time.Time
		if e = rows.Scan(&id, &nick, &body, &status, &reply, &at); e != nil {
			return e
		}
		out = append(out, map[string]any{"id": id, "nickname": nick, "body": body, "status": status, "reply": reply, "createdAt": at})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"messages": out})
	return nil
}
func (a *App) guitarMessage(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Nickname string `json:"nickname"`
		Body     string `json:"body"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	body, e := guitarText(in.Body, 2, 2000)
	if e != nil {
		return e
	}
	if in.Nickname == "" {
		in.Nickname = "匿名琴友"
	}
	nick, e := guitarText(in.Nickname, 1, 30)
	if e != nil {
		return e
	}
	if e = a.guitarWriteLimit(r, "message", 12); e != nil {
		return e
	}
	id := newID()
	_, e = a.db.Exec(r.Context(), `INSERT INTO guitar_messages(id,nickname,body) VALUES($1,$2,$3)`, id, nick, body)
	if e != nil {
		return e
	}
	writeJSON(w, 201, map[string]any{"id": id, "saved": true})
	return nil
}
func (a *App) guitarQuestions(w http.ResponseWriter, r *http.Request) error {
	var exists bool
	if e := a.db.QueryRow(r.Context(), `SELECT EXISTS(SELECT 1 FROM guitar_courses WHERE app_id=$1 AND id=$2 AND status='published')`, guitarAppID, r.PathValue("course")).Scan(&exists); e != nil {
		return e
	}
	if !exists {
		return fail(404, "not_found", "课程不存在。")
	}
	rows, e := a.db.Query(r.Context(), `SELECT q.id::text,q.body,q.lesson_id,COALESCE(l.title,''),'琴友 '||left(replace(q.user_id::text,'-',''),6),q.created_at,COALESCE((SELECT jsonb_agg(jsonb_build_object('id',rr.id,'body',rr.body,'author','琴友 '||left(replace(rr.user_id::text,'-',''),6),'createdAt',rr.created_at) ORDER BY rr.created_at) FROM (SELECT id,body,user_id,created_at FROM guitar_replies WHERE app_id=q.app_id AND question_id=q.id ORDER BY created_at LIMIT 100) rr),'[]'::jsonb) FROM guitar_questions q LEFT JOIN guitar_lessons l ON l.app_id=q.app_id AND l.id=q.lesson_id WHERE q.app_id=$1 AND q.course_id=$2 ORDER BY q.created_at DESC LIMIT 50`, guitarAppID, r.PathValue("course"))
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, body, lt, author string
		var lid *string
		var at time.Time
		var replies json.RawMessage
		if e = rows.Scan(&id, &body, &lid, &lt, &author, &at, &replies); e != nil {
			return e
		}
		out = append(out, map[string]any{"id": id, "body": body, "lessonId": lid, "lessonTitle": lt, "author": author, "createdAt": at, "replies": replies})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"questions": out})
	return nil
}
func (a *App) guitarQuestion(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Body     string  `json:"body"`
		LessonID *string `json:"lessonId"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	body, e := guitarText(in.Body, 2, 2000)
	if e != nil {
		return e
	}
	p := identity(r)
	if e = a.limited(r, "guitar:question:"+p.UserID, 30, time.Hour); e != nil {
		return e
	}
	id := newID()
	tag, e := a.db.Exec(r.Context(), `INSERT INTO guitar_questions(id,user_id,course_id,lesson_id,body) SELECT $1,$2,c.id,$4,$5 FROM guitar_courses c WHERE c.app_id=$6 AND c.id=$3 AND c.status='published' AND ($4::text IS NULL OR EXISTS(SELECT 1 FROM guitar_lessons l JOIN guitar_chapters ch ON ch.app_id=l.app_id AND ch.id=l.chapter_id WHERE l.app_id=c.app_id AND l.id=$4 AND ch.course_id=c.id))`, id, p.UserID, r.PathValue("course"), in.LessonID, body, p.AppID)
	if e != nil {
		return e
	}
	if tag.RowsAffected() == 0 {
		return fail(400, "invalid_context", "课程或关联课时不存在。")
	}
	writeJSON(w, 201, map[string]any{"id": id, "saved": true})
	return nil
}
func (a *App) guitarReply(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Body string `json:"body"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	body, e := guitarText(in.Body, 1, 2000)
	if e != nil {
		return e
	}
	p := identity(r)
	if !validID(r.PathValue("question")) {
		return fail(404, "not_found", "问题不存在。")
	}
	if e = a.limited(r, "guitar:reply:"+p.UserID, 60, time.Hour); e != nil {
		return e
	}
	id := newID()
	tag, e := a.db.Exec(r.Context(), `INSERT INTO guitar_replies(id,user_id,question_id,body) SELECT $1,$2,q.id,$4 FROM guitar_questions q JOIN guitar_courses c ON c.app_id=q.app_id AND c.id=q.course_id WHERE q.app_id=$5 AND q.id=$3 AND c.status='published'`, id, p.UserID, r.PathValue("question"), body, p.AppID)
	if e != nil {
		return e
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "问题不存在。")
	}
	writeJSON(w, 201, map[string]any{"id": id, "saved": true})
	return nil
}
