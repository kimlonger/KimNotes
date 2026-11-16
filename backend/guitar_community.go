package main

import (
	"net/http"
	"time"
)

func (a *App) guitarMessages(w http.ResponseWriter, r *http.Request) error {
	page, e := guitarCatalogNumber(r, "page", 1, 1000000)
	if e != nil {
		return e
	}
	pageSize, e := guitarCatalogNumber(r, "pageSize", 8, 50)
	if e != nil {
		return e
	}
	var total int
	const where = ` WHERE m.app_id=$1 AND m.status<>'hidden' AND m.requested_title IS NULL AND m.question_title IS NULL`
	if e = a.db.QueryRow(r.Context(), `SELECT count(*) FROM guitar_messages m`+where, guitarAppID).Scan(&total); e != nil {
		return e
	}
	rows, e := a.db.Query(r.Context(), `SELECT m.id::text,COALESCE(g.nickname,m.nickname),m.body,m.status,m.reply,m.created_at,COALESCE(g.avatar,'guitar'),CASE WHEN g.avatar_bytes IS NOT NULL THEN '/api/v1/apps/guitar/avatars/'||g.user_id::text||'?v='||extract(epoch FROM g.updated_at)::text ELSE '' END FROM guitar_messages m LEFT JOIN guitar_members g ON g.app_id=m.app_id AND g.user_id=m.author_id`+where+` ORDER BY (m.status='open') DESC,m.created_at DESC,m.id DESC LIMIT $2 OFFSET $3`, guitarAppID, pageSize, (page-1)*pageSize)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, nick, body, status, reply, avatar, avatarURL string
		var at time.Time
		if e = rows.Scan(&id, &nick, &body, &status, &reply, &at, &avatar, &avatarURL); e != nil {
			return e
		}
		out = append(out, map[string]any{"id": id, "nickname": nick, "body": body, "status": status, "reply": reply, "createdAt": at, "avatar": avatar, "avatarUrl": avatarURL})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"messages": out, "pagination": map[string]any{"page": page, "pageSize": pageSize, "total": total, "totalPages": (total + pageSize - 1) / pageSize}})
	return nil
}
func (a *App) guitarMessage(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Nickname string `json:"nickname"`
		Body     string `json:"body"`
		Type     string `json:"type"`
	}
	if e := decode(w, r, &in); e != nil {
		return e
	}
	body, e := guitarText(in.Body, 2, 2000)
	if e != nil {
		return e
	}
	switch in.Type {
	case "":
	case "功能建议", "问题反馈", "缺少曲谱":
		body = "[" + in.Type + "] " + body
	default:
		return fail(400, "invalid_feedback_type", "请选择有效的反馈类型。")
	}
	nick := "匿名琴友"
	var author any
	if p, err := a.authenticate(r); err == nil {
		if expected := r.Header.Get("X-Kim-Expected-User"); expected != "" && expected != p.UserID {
			return fail(409, "account_changed", "账号已切换，请刷新后重试。")
		}
		member, err := a.guitarMember(r.Context(), p.UserID)
		if err != nil {
			return err
		}
		nick = member["nickname"].(string)
		author = p.UserID
	} else if problem, ok := err.(*apiError); !ok || problem.Status != 401 {
		return err
	}
	if e = a.guitarWriteLimit(r, "message", 12); e != nil {
		return e
	}
	id := newID()
	_, e = a.db.Exec(r.Context(), `INSERT INTO guitar_messages(id,nickname,body,author_id) VALUES($1,$2,$3,$4)`, id, nick, body, author)
	if e != nil {
		return e
	}
	writeJSON(w, 201, map[string]any{"id": id, "saved": true})
	return nil
}
