package main

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

func (a *App) canManageGuitar(p principal) bool {
	return a.config.GuitarAdminUserID != "" && p.UserID == a.config.GuitarAdminUserID &&
		p.AppID == guitarAppID && p.ClientKind == "desktop" && p.Protocol == 3
}

func (a *App) guitarAdminRoutes(m *http.ServeMux, prefix string) {
	for _, route := range []struct {
		pattern string
		fn      func(http.ResponseWriter, *http.Request) error
	}{
		{"GET /admin/uploads", a.guitarAdminUploads},
		{"GET /admin/uploads/{upload}/files/{page}", a.guitarAdminUploadFile},
		{"POST /admin/uploads/{upload}/review", a.guitarAdminReviewUpload},
		{"GET /admin/messages", a.guitarAdminMessages},
		{"PATCH /admin/messages/{message}", a.guitarAdminMessage},
		{"GET /admin/artists", a.guitarAdminArtists},
		{"PUT /admin/artists/{artist}/avatar", a.guitarAdminSetArtistPhoto},
	} {
		method, path, _ := strings.Cut(route.pattern, " ")
		m.HandleFunc(method+" "+prefix+path, a.guitarHandler(func(w http.ResponseWriter, r *http.Request) error {
			w.Header().Set("Cache-Control", "private, no-store")
			if !a.canManageGuitar(identity(r)) {
				return fail(403, "guitar_admin_required", "只有吉他管理员可以进行此操作。")
			}
			return route.fn(w, r)
		}, true))
	}
}

const guitarAdminPageSize = 20

func guitarAdminOffset(r *http.Request) int {
	page, _ := strconv.Atoi(r.URL.Query().Get("page"))
	return (min(max(page, 1), 10000) - 1) * guitarAdminPageSize
}

func writeGuitarAdminList[T any](w http.ResponseWriter, items []T) {
	hasMore := len(items) > guitarAdminPageSize
	if hasMore {
		items = items[:guitarAdminPageSize]
	}
	writeJSON(w, 200, map[string]any{"items": items, "hasMore": hasMore})
}

// Retain access to the exact submitted files after the catalogue version changes.
const guitarReviewedFile = `(f.app_id,f.score_id)=(u.app_id,u.score_id)
 AND f.original_upload_id IS NOT DISTINCT FROM (SELECT original_upload_id FROM guitar_score_files
 WHERE app_id=u.app_id AND original_upload_id=u.id LIMIT 1)`

func (a *App) guitarAdminUploads(w http.ResponseWriter, r *http.Request) error {
	status := r.URL.Query().Get("status")
	if status == "" {
		status = "pending"
	}
	if status != "pending" && status != "approved" && status != "rejected" {
		return fail(400, "invalid_review_status", "审核状态无效。")
	}
	rows, err := a.db.Query(r.Context(), `SELECT u.id::text,s.title,s.artist,s.category,s.difficulty_stars,u.created_at,
 u.reviewed_at,COALESCE(u.review_note,''),COALESCE(m.nickname,'琴友'),COALESCE(f.mime,''),COALESCE(f.pages,0)
 FROM guitar_uploads u JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)
 LEFT JOIN guitar_members m ON (m.app_id,m.user_id)=(u.app_id,u.user_id)
 LEFT JOIN LATERAL (SELECT count(*)::int AS pages,min(f.mime) FILTER (WHERE f.file_no=1) AS mime
 FROM guitar_score_files f WHERE `+guitarReviewedFile+`) f ON true
 WHERE u.app_id='guitar' AND u.status=$1
 ORDER BY CASE WHEN u.status='pending' THEN u.created_at END ASC,
 u.reviewed_at DESC NULLS LAST,u.id LIMIT $2 OFFSET $3`, status, guitarAdminPageSize+1, guitarAdminOffset(r))
	if err != nil {
		return err
	}
	defer rows.Close()
	items := []map[string]any{}
	for rows.Next() {
		var id, title, artist, category, note, nickname, mime string
		var stars *float64
		var created time.Time
		var reviewed *time.Time
		var pages int
		if err := rows.Scan(&id, &title, &artist, &category, &stars, &created, &reviewed, &note, &nickname, &mime, &pages); err != nil {
			return err
		}
		items = append(items, map[string]any{"id": id, "title": title, "artist": artist, "category": category,
			"difficultyStars": stars, "createdAt": created, "reviewedAt": reviewed, "reviewNote": note, "nickname": nickname,
			"mime": mime, "pages": pages, "status": status})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeGuitarAdminList(w, items)
	return nil
}

func (a *App) guitarAdminUploadFile(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("upload")
	page, err := strconv.Atoi(r.PathValue("page"))
	if !validID(id) || err != nil || page < 1 || page > 12 {
		return fail(404, "not_found", "谱面不存在。")
	}
	var mime string
	var data []byte
	if err := a.db.QueryRow(r.Context(), `SELECT f.mime,f.bytes FROM guitar_uploads u
 JOIN guitar_score_files f ON `+guitarReviewedFile+`
 WHERE u.app_id='guitar' AND u.id=$1 AND f.file_no=$2`, id, page).Scan(&mime, &data); err != nil {
		return err
	}
	w.Header().Set("Content-Type", mime)
	w.Header().Set("X-Content-Type-Options", "nosniff")
	http.ServeContent(w, r, "", time.Time{}, bytes.NewReader(data))
	return nil
}

func (a *App) guitarAdminReviewUpload(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("upload")
	if !validID(id) {
		return fail(404, "not_found", "上传记录不存在。")
	}
	var change struct {
		Decision string `json:"decision"`
		Note     string `json:"note"`
	}
	if err := decode(w, r, &change); err != nil {
		return err
	}
	if change.Decision != "approve" && change.Decision != "reject" {
		return fail(400, "invalid_review_action", "请选择通过或驳回。")
	}
	note := ""
	if change.Decision == "reject" {
		var err error
		note, err = guitarText(change.Note, 1, 300)
		if err != nil {
			return err
		}
	}
	if err := reviewGuitarUpload(r.Context(), a.db, id, change.Decision, note); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}

func (a *App) guitarAdminMessages(w http.ResponseWriter, r *http.Request) error {
	kind := r.URL.Query().Get("kind")
	if kind != "suggestions" && kind != "questions" && kind != "requests" {
		return fail(400, "invalid_message_kind", "请选择建议、问答或求谱。")
	}
	rows, err := a.db.Query(r.Context(), `SELECT m.id::text,m.nickname,COALESCE(m.question_title,m.requested_title,''),
 COALESCE(m.requested_artist,''),m.body,m.status,m.reply,m.created_at
 FROM guitar_messages m WHERE m.app_id='guitar' AND (`+guitarCommunityKind+`)=$1
 ORDER BY (m.status='open') DESC,(m.status='hidden') ASC,m.created_at DESC,m.id LIMIT $2 OFFSET $3`,
		kind, guitarAdminPageSize+1, guitarAdminOffset(r))
	if err != nil {
		return err
	}
	defer rows.Close()
	items := []map[string]any{}
	for rows.Next() {
		var id, nickname, title, artist, body, status, reply string
		var created time.Time
		if err := rows.Scan(&id, &nickname, &title, &artist, &body, &status, &reply, &created); err != nil {
			return err
		}
		items = append(items, map[string]any{"id": id, "nickname": nickname, "title": title, "artist": artist,
			"body": body, "status": status, "reply": reply, "createdAt": created})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeGuitarAdminList(w, items)
	return nil
}

func moderateGuitarMessage(ctx context.Context, db *pgxpool.Pool, id, status, reply string, restore bool) error {
	tag, err := db.Exec(ctx, `UPDATE guitar_messages SET status=$2,reply=$3,updated_at=now()
 WHERE app_id='guitar' AND id=$1 AND ((requested_title IS NULL AND question_title IS NULL)
 OR $2='hidden' OR ($4 AND $2='open' AND status='hidden'))`, id, status, reply, restore)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "帖子不存在，或不支持这项操作。")
	}
	return nil
}

func (a *App) guitarAdminMessage(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("message")
	if !validID(id) {
		return fail(404, "not_found", "帖子不存在。")
	}
	var in struct {
		Status string `json:"status"`
		Reply  string `json:"reply"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	switch in.Status {
	case "open", "accepted", "done", "hidden":
	default:
		return fail(400, "invalid_message_status", "帖子状态无效。")
	}
	reply, err := guitarText(in.Reply, 0, 2000)
	if err != nil {
		return err
	}
	if err := moderateGuitarMessage(r.Context(), a.db, id, in.Status, reply, true); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}

func (a *App) guitarAdminArtists(w http.ResponseWriter, r *http.Request) error {
	query, err := guitarText(r.URL.Query().Get("q"), 0, 120)
	if err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT id::text,name,avatar_bytes IS NOT NULL FROM guitar_artists
 WHERE app_id='guitar' AND strpos(lower(name),lower($1))>0
 ORDER BY name,id LIMIT $2 OFFSET $3`, query, guitarAdminPageSize+1, guitarAdminOffset(r))
	if err != nil {
		return err
	}
	defer rows.Close()
	items := []map[string]any{}
	for rows.Next() {
		var id, name string
		var photo bool
		if err := rows.Scan(&id, &name, &photo); err != nil {
			return err
		}
		items = append(items, map[string]any{"id": id, "name": name, "hasPhoto": photo})
	}
	if err := rows.Err(); err != nil {
		return err
	}
	writeGuitarAdminList(w, items)
	return nil
}

func (a *App) guitarAdminSetArtistPhoto(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("artist")
	if !validID(id) {
		return fail(404, "not_found", "歌手不存在。")
	}
	const maxBytes = 512 << 10
	r.Body = http.MaxBytesReader(w, r.Body, maxBytes)
	data, err := io.ReadAll(r.Body)
	if err != nil {
		var oversized *http.MaxBytesError
		if errors.As(err, &oversized) {
			return fail(413, "artist_photo_too_large", "请上传 512 KB 以内的图片。")
		}
		return err
	}
	if err := setGuitarArtistPhoto(r.Context(), a.db, id, data); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]bool{"saved": true})
	return nil
}
