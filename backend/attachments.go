package main

import (
	"bytes"
	"errors"
	"github.com/jackc/pgx/v5"
	_ "golang.org/x/image/webp"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"net/http"
	"strconv"
	"time"
)

func (a *App) upload(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if e := a.limited(r, "uploads:"+p.AppID+":"+p.UserID, 30, time.Minute); e != nil {
		return e
	}
	r.Body = http.MaxBytesReader(w, r.Body, 10<<20)
	data, e := io.ReadAll(r.Body)
	if e != nil || len(data) == 0 {
		return fail(413, "image_size", "图片需要小于 10 MB。")
	}
	cfg, format, e := image.DecodeConfig(bytes.NewReader(data))
	if e != nil || cfg.Width < 1 || cfg.Height < 1 || int64(cfg.Width)*int64(cfg.Height) > 40000000 {
		return fail(400, "invalid_image", "图片无效，或超过 4000 万像素。")
	}
	mime := map[string]string{"png": "image/png", "jpeg": "image/jpeg", "gif": "image/gif", "webp": "image/webp"}[format]
	if mime == "" {
		return fail(415, "invalid_image", "仅支持 PNG、JPEG、GIF、WebP。")
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var cursor int64
	if e = tx.QueryRow(r.Context(), "SELECT cursor FROM user_apps WHERE app_id=$1 AND user_id=$2 FOR UPDATE", p.AppID, p.UserID).Scan(&cursor); e != nil {
		return e
	}
	sha := digest(string(data))
	var existing string
	e = tx.QueryRow(r.Context(), "SELECT id::text FROM attachments WHERE app_id=$1 AND user_id=$2 AND sha256=$3 ORDER BY created_at LIMIT 1", p.AppID, p.UserID, sha).Scan(&existing)
	if e == nil {
		writeJSON(w, 200, map[string]any{"id": existing, "mime": mime, "sha256": sha, "width": cfg.Width, "height": cfg.Height, "size": len(data)})
		return nil
	}
	if !errors.Is(e, pgx.ErrNoRows) {
		return e
	}
	var used int64
	if e = tx.QueryRow(r.Context(), `SELECT COALESCE((SELECT sum(octet_length(bytes)) FROM attachments WHERE app_id=$1 AND user_id=$2),0)+COALESCE((SELECT sum(octet_length(snapshot::text)) FROM note_versions WHERE app_id=$1 AND user_id=$2),0)`, p.AppID, p.UserID).Scan(&used); e != nil {
		return e
	}
	if used+int64(len(data)) > 256<<20 {
		return fail(413, "storage_limit", "云端存储已达上限。")
	}
	id := newID()
	if _, e = tx.Exec(r.Context(), "INSERT INTO attachments(app_id,user_id,id,mime,sha256,bytes,width,height) VALUES($1,$2,$3,$4,$5,$6,$7,$8)", p.AppID, p.UserID, id, mime, sha, data, cfg.Width, cfg.Height); e != nil {
		return e
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 201, map[string]any{"id": id, "mime": mime, "sha256": sha, "width": cfg.Width, "height": cfg.Height, "size": len(data)})
	return nil
}
func (a *App) download(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	id := r.PathValue("attachment")
	if !validID(id) {
		return fail(404, "not_found", "图片不存在。")
	}
	var mime, hash string
	var data []byte
	if e := a.db.QueryRow(r.Context(), "SELECT mime,sha256,bytes FROM attachments WHERE app_id=$1 AND user_id=$2 AND id=$3", p.AppID, p.UserID, id).Scan(&mime, &hash, &data); e != nil {
		return e
	}
	ext := map[string]string{"image/png": "png", "image/jpeg": "jpg", "image/gif": "gif", "image/webp": "webp"}[mime]
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Content-Length", strconv.Itoa(len(data)))
	w.Header().Set("Content-Disposition", `inline; filename="`+id+`.`+ext+`"`)
	w.Header().Set("Content-Security-Policy", "default-src 'none'; sandbox")
	w.Header().Set("ETag", `"`+hash+`"`)
	w.WriteHeader(200)
	_, e := w.Write(data)
	return e
}
