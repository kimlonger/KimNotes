package main

import (
	"net/http"
	"time"
)

const guitarCommunityKind = `CASE WHEN m.requested_title IS NOT NULL THEN 'requests' WHEN m.question_title IS NOT NULL THEN 'questions' ELSE 'suggestions' END`
const guitarVisibleQuestion = `m.app_id='guitar' AND m.question_title IS NOT NULL AND m.status<>'hidden'`

func (a *App) guitarCommunityFeed(w http.ResponseWriter, r *http.Request) error {
	kind := r.URL.Query().Get("kind")
	switch kind {
	case "", "all":
		kind = "all"
	case "questions", "requests", "suggestions":
	default:
		return fail(400, "invalid_topic", "请选择问答、求谱或建议。")
	}
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	const size = 12
	const where = ` WHERE m.app_id='guitar' AND m.status<>'hidden' AND ($1='all' OR ` + guitarCommunityKind + `=$1)`
	var total int
	if err := a.db.QueryRow(r.Context(), `SELECT count(*) FROM guitar_messages m`+where, kind).Scan(&total); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT m.id::text,`+guitarCommunityKind+`,
 COALESCE(m.question_title,m.requested_title,''),COALESCE(m.requested_artist,''),
 CASE WHEN m.requested_title IS NULL THEN m.body ELSE `+guitarRequestNote+` END,
 COALESCE(g.nickname,m.nickname),m.created_at,m.status,
 CASE WHEN m.requested_title IS NOT NULL THEN answers.count ELSE
 (SELECT count(*) FROM guitar_community_answers a WHERE a.app_id=m.app_id AND a.question_id=m.id) END,
 COALESCE(answers.solution,''),CASE WHEN m.requested_title IS NULL AND m.question_title IS NULL THEN m.reply ELSE '' END
 FROM guitar_messages m LEFT JOIN guitar_members g ON g.app_id=m.app_id AND g.user_id=m.author_id`+
		guitarRequestAnswersJoin+where+`
 ORDER BY CASE WHEN $1='suggestions' THEN (m.status='open') WHEN $1='requests' THEN (answers.solution IS NULL) END DESC,
 m.created_at DESC,m.id DESC LIMIT $2 OFFSET $3`, kind, size, (page-1)*size)
	if err != nil {
		return err
	}
	defer rows.Close()
	topics := []map[string]any{}
	for rows.Next() {
		var id, topicKind, title, artist, body, nickname, status, solution, reply string
		var created time.Time
		var count int
		if err := rows.Scan(&id, &topicKind, &title, &artist, &body, &nickname, &created, &status, &count, &solution, &reply); err != nil {
			return err
		}
		if topicKind == "requests" {
			status = "open"
			if solution != "" {
				status = "solved"
			}
		}
		topics = append(topics, map[string]any{"id": id, "kind": topicKind, "title": title, "artist": artist, "body": body,
			"nickname": nickname, "createdAt": created, "status": status, "answerCount": count, "solutionScoreId": solution, "reply": reply})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"topics": topics, "pagination": guitarHomePagination(page, size, total)})
	return nil
}

func (a *App) guitarAskCommunityQuestion(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Title string `json:"title"`
		Body  string `json:"body"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	title, err := guitarText(in.Title, 1, 100)
	if err != nil {
		return err
	}
	body, err := guitarText(in.Body, 2, 2000)
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
	id := newID()
	if _, err = a.db.Exec(r.Context(), `INSERT INTO guitar_messages(id,nickname,body,author_id,question_title) VALUES($1,$2,$3,$4,$5)`,
		id, member["nickname"], body, user, title); err != nil {
		return err
	}
	writeJSON(w, 201, map[string]string{"id": id})
	return nil
}

func (a *App) guitarCommunityQuestion(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("question")
	if !validID(id) {
		return fail(404, "not_found", "问题不存在。")
	}
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	const size = 20
	var title, body, nickname string
	var created time.Time
	var total int
	err = a.db.QueryRow(r.Context(), `SELECT m.question_title,m.body,COALESCE(g.nickname,m.nickname),m.created_at,
 (SELECT count(*) FROM guitar_community_answers a WHERE a.app_id=m.app_id AND a.question_id=m.id)
 FROM guitar_messages m LEFT JOIN guitar_members g ON g.app_id=m.app_id AND g.user_id=m.author_id
 WHERE `+guitarVisibleQuestion+` AND m.id=$1`, id).Scan(&title, &body, &nickname, &created, &total)
	if err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT a.id::text,a.body,COALESCE(g.nickname,'琴友'),a.created_at
 FROM guitar_community_answers a LEFT JOIN guitar_members g ON g.app_id=a.app_id AND g.user_id=a.author_id
 WHERE a.app_id='guitar' AND a.question_id=$1
 ORDER BY a.created_at DESC,a.id DESC LIMIT $2 OFFSET $3`, id, size, (page-1)*size)
	if err != nil {
		return err
	}
	defer rows.Close()
	answers := []map[string]any{}
	for rows.Next() {
		var id, body, nickname string
		var at time.Time
		if err := rows.Scan(&id, &body, &nickname, &at); err != nil {
			return err
		}
		answers = append(answers, map[string]any{"id": id, "body": body, "nickname": nickname, "createdAt": at})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"question": map[string]any{"id": id, "title": title, "body": body, "nickname": nickname, "createdAt": created},
		"answers": answers, "pagination": guitarHomePagination(page, size, total)})
	return nil
}

func (a *App) guitarAnswerCommunityQuestion(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("question")
	if !validID(id) {
		return fail(404, "not_found", "问题不存在。")
	}
	var in struct {
		Body string `json:"body"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	body, err := guitarText(in.Body, 2, 2000)
	if err != nil {
		return err
	}
	if err := a.guitarWriteLimit(r, "community-answer", 40); err != nil {
		return err
	}
	tag, err := a.db.Exec(r.Context(), `INSERT INTO guitar_community_answers(id,question_id,author_id,body)
 SELECT $1,m.id,$2,$3 FROM guitar_messages m WHERE `+guitarVisibleQuestion+` AND m.id=$4`,
		newID(), identity(r).UserID, body, id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "问题不存在。")
	}
	writeJSON(w, 201, map[string]bool{"saved": true})
	return nil
}
