package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"github.com/jackc/pgx/v5"
	"image"
	"net/http"
	"strings"
	"time"
)

func guitarMemberValues(nickname, avatar string) (string, string, error) {
	nickname = strings.TrimSpace(nickname)
	if nickname == "" {
		nickname = "琴友"
	}
	name, err := guitarText(nickname, 1, 20)
	if err != nil {
		return "", "", err
	}
	if avatar == "" {
		avatar = "guitar"
	}
	switch avatar {
	case "guitar", "vinyl", "cat", "leaf", "wave", "sun":
	default:
		return "", "", fail(400, "invalid_avatar", "请选择有效头像。")
	}
	return name, avatar, nil
}
func saveGuitarMember(ctx context.Context, tx pgx.Tx, app, id, nickname, avatar string) error {
	if app != guitarAppID {
		return nil
	}
	nickname, avatar, err := guitarMemberValues(nickname, avatar)
	if err != nil {
		return err
	}
	_, err = tx.Exec(ctx, `INSERT INTO guitar_members(user_id,nickname,avatar) VALUES($1,$2,$3) ON CONFLICT DO NOTHING`, id, nickname, avatar)
	return err
}
func (a *App) guitarMember(ctx context.Context, id string) (map[string]any, error) {
	var name, avatar string
	var hasImage bool
	var at time.Time
	err := a.db.QueryRow(ctx, `SELECT COALESCE(m.nickname,'琴友'),COALESCE(m.avatar,'guitar'),m.avatar_bytes IS NOT NULL,COALESCE(m.updated_at,u.created_at) FROM users u LEFT JOIN guitar_members m ON m.app_id='guitar' AND m.user_id=u.id WHERE u.id=$1`, id).Scan(&name, &avatar, &hasImage, &at)
	if err != nil {
		return nil, err
	}
	avatarURL := ""
	if hasImage {
		avatarURL = fmt.Sprintf("/api/v1/apps/guitar/avatars/%s?v=%d", id, at.UnixMicro())
	}
	return map[string]any{"nickname": name, "avatar": avatar, "avatarUrl": avatarURL}, nil
}
func (a *App) guitarIdentity(w http.ResponseWriter, r *http.Request) error {
	member, err := a.guitarMember(r.Context(), identity(r).UserID)
	if err != nil {
		return err
	}
	writeJSON(w, 200, member)
	return nil
}
func (a *App) guitarSetIdentity(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Nickname    string `json:"nickname"`
		Avatar      string `json:"avatar"`
		ResetAvatar bool   `json:"resetAvatar"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	name, avatar, err := guitarMemberValues(in.Nickname, in.Avatar)
	if err != nil {
		return err
	}
	_, err = a.db.Exec(r.Context(), `INSERT INTO guitar_members(user_id,nickname,avatar) VALUES($1,$2,$3) ON CONFLICT(app_id,user_id) DO UPDATE SET nickname=EXCLUDED.nickname,avatar=EXCLUDED.avatar,avatar_bytes=CASE WHEN $4 THEN NULL ELSE guitar_members.avatar_bytes END,avatar_mime=CASE WHEN $4 THEN NULL ELSE guitar_members.avatar_mime END,updated_at=now()`, identity(r).UserID, name, avatar, in.ResetAvatar)
	if err != nil {
		return err
	}
	return a.guitarIdentity(w, r)
}
func (a *App) guitarSetAvatar(w http.ResponseWriter, r *http.Request) error {
	if err := a.guitarWriteLimit(r, "avatar", 20); err != nil {
		return err
	}
	var in struct {
		DataURL string `json:"dataUrl"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if len(in.DataURL) > 710000 {
		return fail(400, "invalid_avatar", "头像不能超过512 KiB。")
	}
	parts := strings.SplitN(in.DataURL, ",", 2)
	if len(parts) != 2 {
		return fail(400, "invalid_avatar", "头像格式无效。")
	}
	data, err := base64.StdEncoding.DecodeString(parts[1])
	if err != nil || len(data) == 0 || len(data) > 524288 {
		return fail(400, "invalid_avatar", "头像格式无效或过大。")
	}
	mime := http.DetectContentType(data)
	if mime != "image/png" && mime != "image/jpeg" && mime != "image/webp" {
		return fail(400, "invalid_avatar", "头像需为PNG、JPG或WebP图片。")
	}
	config, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil || config.Width < 1 || config.Height < 1 || config.Width > 2048 || config.Height > 2048 {
		return fail(400, "invalid_avatar", "头像像素过大或图片损坏。")
	}
	_, err = a.db.Exec(r.Context(), `INSERT INTO guitar_members(user_id,avatar_bytes,avatar_mime) VALUES($1,$2,$3) ON CONFLICT(app_id,user_id) DO UPDATE SET avatar_bytes=EXCLUDED.avatar_bytes,avatar_mime=EXCLUDED.avatar_mime,updated_at=now()`, identity(r).UserID, data, mime)
	if err != nil {
		return err
	}
	return a.guitarIdentity(w, r)
}
func (a *App) guitarAvatar(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("user")
	if !validID(id) {
		return fail(404, "not_found", "头像不存在。")
	}
	var data []byte
	var mime string
	if err := a.db.QueryRow(r.Context(), `SELECT avatar_bytes,avatar_mime FROM guitar_members WHERE app_id='guitar' AND user_id=$1 AND avatar_bytes IS NOT NULL`, id).Scan(&data, &mime); err != nil {
		return err
	}
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.WriteHeader(200)
	_, err := w.Write(data)
	return err
}
func (a *App) guitarPracticeStart(w http.ResponseWriter, r *http.Request) error {
	if err := a.guitarWriteLimit(r, "practice", 120); err != nil {
		return err
	}
	var in struct {
		ScoreID   string `json:"scoreId"`
		Automatic bool   `json:"automatic"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	id := newID()
	tag, err := a.db.Exec(r.Context(), `INSERT INTO guitar_practice_sessions(id,user_id,score_id,automatic) SELECT $1,$2,id,$4 FROM guitar_scores WHERE app_id='guitar' AND id=$3 AND status='published'`, id, identity(r).UserID, in.ScoreID, in.Automatic)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "曲谱不存在。")
	}
	writeJSON(w, 201, map[string]any{"id": id})
	return nil
}
func (a *App) guitarPracticeStop(w http.ResponseWriter, r *http.Request) error {
	if !validID(r.PathValue("practice")) {
		return fail(404, "not_found", "练习记录不存在。")
	}
	var in struct {
		ActiveSeconds *int `json:"activeSeconds"`
	}
	if r.ContentLength != 0 {
		if err := decode(w, r, &in); err != nil {
			return err
		}
	}
	var seconds int
	err := a.db.QueryRow(r.Context(), `UPDATE guitar_practice_sessions SET ended_at=COALESCE(ended_at,now()),seconds=CASE
 WHEN ended_at IS NOT NULL THEN seconds
 WHEN automatic AND LEAST(COALESCE($3::int,0),extract(epoch FROM now()-started_at)::integer+2)<60 THEN 0
 WHEN automatic THEN LEAST(21600,$3::int,extract(epoch FROM now()-started_at)::integer+2)
 ELSE LEAST(21600,GREATEST(0,extract(epoch FROM now()-started_at)::integer)) END
 WHERE app_id='guitar' AND user_id=$1 AND id=$2 RETURNING seconds`, identity(r).UserID, r.PathValue("practice"), in.ActiveSeconds).Scan(&seconds)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"seconds": seconds})
	return nil
}
func (a *App) guitarPracticeOverview(w http.ResponseWriter, r *http.Request) error {
	user := identity(r).UserID
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	pageSize, err := guitarCatalogNumber(r, "pageSize", 6, 24)
	if err != nil {
		return err
	}
	var seconds, count, today, practiced int
	if err = a.db.QueryRow(r.Context(), `SELECT COALESCE(sum(seconds),0),count(*),COALESCE(sum(seconds) FILTER(WHERE (started_at AT TIME ZONE 'Asia/Shanghai')::date=(now() AT TIME ZONE 'Asia/Shanghai')::date),0),count(DISTINCT score_id) FROM guitar_practice_sessions WHERE app_id='guitar' AND user_id=$1 AND ended_at IS NOT NULL AND seconds>0`, user).Scan(&seconds, &count, &today, &practiced); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT p.score_id,COALESCE(s.title,'曲谱已下架'),sum(p.seconds),max(p.started_at),count(*),s.id IS NOT NULL,`+guitarCardColumns+`
 FROM guitar_practice_sessions p LEFT JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id AND s.status='published'
 WHERE p.app_id='guitar' AND p.user_id=$1 AND p.ended_at IS NOT NULL AND p.seconds>0
 GROUP BY p.score_id,s.app_id,s.id ORDER BY max(p.started_at) DESC,p.score_id LIMIT $2 OFFSET $3`, user, pageSize, (page-1)*pageSize)
	if err != nil {
		return err
	}
	defer rows.Close()
	recent := []map[string]any{}
	for rows.Next() {
		var id, title string
		var duration, sessions int
		var at time.Time
		var available bool
		var card guitarCard
		if err = rows.Scan(append([]any{&id, &title, &duration, &at, &sessions, &available}, card.scanFields()...)...); err != nil {
			return err
		}
		row := map[string]any{"id": id, "title": title, "seconds": duration, "at": at, "lastPracticedAt": at, "sessions": sessions, "available": available}
		card.apply(row)
		recent = append(recent, row)
	}
	if err = rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"totalSeconds": seconds, "todaySeconds": today, "sessions": count, "practicedScores": practiced, "recent": recent, "recentPagination": map[string]any{"page": page, "pageSize": pageSize, "total": practiced, "totalPages": (practiced + pageSize - 1) / pageSize}})
	return nil
}
