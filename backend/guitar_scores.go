package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"github.com/jackc/pgx/v5/pgxpool"
	_ "golang.org/x/image/webp"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"mime"
	"net/http"
	"os"
	"path"
	"strconv"
	"strings"
	"time"
)

const guitarPrivateUploadJoin = `LEFT JOIN guitar_uploads u ON (u.app_id,u.score_id)=(s.app_id,s.id)
 AND u.status IN ('pending','rejected') AND s.status='private' AND u.user_id=`

// userParameter is a fixed SQL placeholder chosen by the caller, never user input.
func guitarScoreVisibleSQL(userParameter string) string {
	return `(s.status='published' OR (s.status='private' AND EXISTS (
 SELECT 1 FROM guitar_uploads u WHERE (u.app_id,u.score_id)=(s.app_id,s.id)
 AND u.user_id=` + userParameter + ` AND u.status IN ('pending','rejected'))))`
}

func (a *App) guitarScore(w http.ResponseWriter, r *http.Request) error {
	var id, title, artist, desc, category, difficulty, credit string
	var mt *string
	var notation json.RawMessage
	var difficultySource string
	var stars float64
	var attributes guitarScoreAttributes
	var privateStatus *string
	e := a.db.QueryRow(r.Context(), `SELECT s.id,s.title,s.artist,s.description,s.category,s.difficulty,s.credit,
 s.notation,`+guitarFirstFileMime+`,`+guitarStarsSQL+`,s.attributes,s.difficulty_source,u.status
 FROM guitar_scores s `+guitarPrivateUploadJoin+`$3
 WHERE s.app_id=$1 AND s.id=$2 AND (s.status='published' OR (s.status='private' AND u.id IS NOT NULL))`, guitarAppID, r.PathValue("score"), identity(r).UserID).Scan(&id, &title, &artist, &desc, &category, &difficulty, &credit, &notation, &mt, &stars, &attributes, &difficultySource, &privateStatus)
	if e != nil {
		return e
	}
	pages, err := a.guitarScorePages(r, id, mt)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"id": id, "title": title, "artist": artist, "description": desc, "category": category, "difficulty": difficulty, "difficultyStars": stars, "difficultySource": difficultySource, "credit": credit, "notation": notation, "mime": mt, "pages": pages, "key": attributes.Key, "capo": attributes.Capo, "meter": attributes.Meter, "bpm": attributes.BPM, "chords": attributes.Chords, "privateStatus": privateStatus})
	return nil
}
func (a *App) guitarScoreFile(w http.ResponseWriter, r *http.Request) error {
	var mt, title string
	var data []byte
	e := a.db.QueryRow(r.Context(), `SELECT f.mime,s.title,f.bytes FROM guitar_scores s
 JOIN guitar_score_files f ON `+guitarCanonicalFile+` AND f.file_no=1
 WHERE s.app_id=$1 AND s.id=$2 AND `+guitarScoreVisibleSQL("$3"), guitarAppID, r.PathValue("score"), identity(r).UserID).Scan(&mt, &title, &data)
	if e != nil {
		return e
	}
	if e = a.guitarReaderBudget(r, len(data)); e != nil {
		return e
	}
	w.Header().Set("Content-Type", mt)
	w.Header().Set("Content-Disposition", mime.FormatMediaType("inline", map[string]string{"filename": title}))
	w.Header().Set("Content-Security-Policy", "sandbox; default-src 'none'")
	w.WriteHeader(200)
	_, e = w.Write(data)
	return e
}
func (a *App) guitarUploads(w http.ResponseWriter, r *http.Request) error {
	page, e := guitarCatalogNumber(r, "page", 1, 1000000)
	if e != nil {
		return e
	}
	size, e := guitarCatalogNumber(r, "pageSize", 200, 200)
	if e != nil {
		return e
	}
	p := identity(r)
	const mine = ` FROM guitar_uploads WHERE app_id=$1 AND user_id=$2`
	var total int
	if e = a.db.QueryRow(r.Context(), `SELECT count(*)`+mine, p.AppID, p.UserID).Scan(&total); e != nil {
		return e
	}
	rows, e := a.db.Query(r.Context(), `SELECT u.id::text,COALESCE(u.original_metadata->>'title',s.title),u.status,u.review_note,
 CASE WHEN u.status='approved' THEN u.score_id END,u.created_at,
 CASE WHEN s.status='private' AND u.status IN ('pending','rejected') THEN u.score_id END
 FROM guitar_uploads u JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)
 WHERE u.app_id=$1 AND u.user_id=$2 ORDER BY u.created_at DESC,u.id LIMIT $3 OFFSET $4`, p.AppID, p.UserID, size, (page-1)*size)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, status, note string
		var score, previewScore *string
		var at time.Time
		if e = rows.Scan(&id, &title, &status, &note, &score, &at, &previewScore); e != nil {
			return e
		}
		out = append(out, map[string]any{"id": id, "title": title, "status": status, "reviewNote": note, "publishedScoreId": score, "previewScoreId": previewScore, "createdAt": at})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"uploads": out, "pagination": guitarHomePagination(page, size, total)})
	return nil
}
func (a *App) guitarUpload(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if e := a.limited(r, "guitar:upload:"+p.UserID, 20, 24*time.Hour); e != nil {
		return e
	}
	r.Body = http.MaxBytesReader(w, r.Body, 11<<20)
	if e := r.ParseMultipartForm(1 << 20); e != nil {
		return fail(400, "invalid_upload", "请选择不超过 10 MiB 的图片或 PDF。")
	}
	defer r.MultipartForm.RemoveAll()
	title, e := guitarText(r.FormValue("title"), 1, 100)
	if e != nil {
		return e
	}
	desc, e := guitarText(r.FormValue("description"), 0, 2000)
	if e != nil {
		return e
	}
	artist, e := guitarText(r.FormValue("artist"), 0, 120)
	if e != nil {
		return e
	}
	category := r.FormValue("category")
	if category != "弹唱" && category != "指弹" && category != "练习" {
		return fail(400, "invalid_category", "曲谱分类无效。")
	}
	rating := r.FormValue("difficultyStars")
	stars, difficulty, e := guitarUploadDifficulty(rating, r.FormValue("difficulty"))
	if e != nil {
		return e
	}
	attributes, e := guitarUploadAttributes(r)
	if e != nil {
		return e
	}
	if r.FormValue("permission") != "confirmed" {
		return fail(400, "permission_required", "请确认你有权分享这份曲谱。")
	}
	f, header, e := r.FormFile("file")
	if e != nil {
		return fail(400, "file_required", "请选择曲谱文件。")
	}
	defer f.Close()
	data, e := io.ReadAll(io.LimitReader(f, (10<<20)+1))
	if e != nil {
		return e
	}
	if len(data) == 0 || len(data) > 10<<20 {
		return fail(400, "invalid_size", "文件需在 10 MiB 内。")
	}
	mt := http.DetectContentType(data)
	if mt != "image/png" && mt != "image/jpeg" && mt != "image/webp" && mt != "application/pdf" {
		return fail(415, "unsupported_file", "支持 PNG、JPG、WebP 和 PDF。")
	}
	if strings.HasPrefix(mt, "image/") {
		cfg, _, e := image.DecodeConfig(bytes.NewReader(data))
		if e != nil || cfg.Width < 1 || cfg.Height < 1 || int64(cfg.Width)*int64(cfg.Height) > 40_000_000 {
			return fail(400, "invalid_image", "图片无效或像素过大。")
		}
	}
	extra, e := guitarExtraPages(r.MultipartForm.File["file"], mt, len(data))
	if e != nil {
		return e
	}
	id := newID()
	scoreID := "upload-" + id
	filename := path.Base(strings.ReplaceAll(header.Filename, "\\", "/"))
	if len(filename) > 240 {
		filename = "score"
	}
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		return e
	}
	defer tx.Rollback(r.Context())
	var requestID any
	if request := strings.TrimSpace(r.FormValue("requestId")); request != "" {
		if _, e = lockGuitarRequest(r.Context(), tx, request); e != nil {
			return e
		}
		solution, err := guitarRequestSolution(r.Context(), tx, request)
		if err != nil {
			return err
		}
		if solution != "" {
			return fail(409, "request_solved", "这条求谱已解决。")
		}
		requestID = request
	}
	format := "images"
	if mt == "application/pdf" {
		format = "pdf"
	}
	difficultySource := "submitted"
	if rating == "" {
		difficultySource = "legacy"
	}
	credit := "琴友 " + strings.ReplaceAll(p.UserID, "-", "")[:6]
	_, e = tx.Exec(r.Context(), `INSERT INTO guitar_scores(id,title,description,category,difficulty,credit,status,artist,
 difficulty_stars,difficulty_source,attributes,file_format) VALUES($1,$2,$3,$4,$5,$6,'private',$7,$8,$9,$10,$11)`,
		scoreID, title, desc, category, difficulty, credit, artist, stars, difficultySource, attributes, format)
	if e != nil {
		return e
	}
	_, e = tx.Exec(r.Context(), `INSERT INTO guitar_uploads(id,user_id,score_id,filename,permission_confirmed_at,request_id)
 VALUES($1,$2,$3,$4,now(),$5)`, id, p.UserID, scoreID, filename, requestID)
	if e != nil {
		return e
	}
	for _, page := range append([]guitarImagePage{{mt, data, 1}}, extra...) {
		if _, e = tx.Exec(r.Context(), `INSERT INTO guitar_score_files(score_id,file_no,mime,bytes) VALUES($1,$2,$3,$4)`, scoreID, page.page, page.mime, page.data); e != nil {
			return e
		}
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 201, map[string]any{"id": id, "status": "pending", "previewScoreId": scoreID})
	return nil
}
func (a *App) guitarRemoveUpload(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if !validID(r.PathValue("upload")) {
		return fail(404, "not_found", "上传记录不存在。")
	}
	tag, e := a.db.Exec(r.Context(), `DELETE FROM guitar_uploads WHERE app_id=$1 AND user_id=$2 AND id=$3`, p.AppID, p.UserID, r.PathValue("upload"))
	if e != nil {
		return e
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "上传记录不存在。")
	}
	writeJSON(w, 200, map[string]bool{"removed": true, "publicResourcePreserved": true})
	return nil
}

// The operator CLI and authenticated admin API share the same moderation actions.
func reviewGuitar(ctx context.Context, db *pgxpool.Pool, args []string) error {
	if len(args) > 0 && args[0] == "artist-photo" {
		return guitarSaveArtistPhoto(ctx, db, args[1:])
	}
	if len(args) == 1 && args[0] == "messages" {
		rows, err := db.Query(ctx, `SELECT id::text,status,nickname,body FROM guitar_messages
 WHERE app_id='guitar' AND requested_title IS NULL AND question_title IS NULL AND status<>'hidden'
 ORDER BY (status='open') DESC,created_at DESC,id LIMIT 100`)
		if err != nil {
			return err
		}
		defer rows.Close()
		for rows.Next() {
			var id, status, name, body string
			if err := rows.Scan(&id, &status, &name, &body); err != nil {
				return err
			}
			fmt.Printf("%s  %s  %q  %q\n", id, status, name, strings.Join(strings.Fields(body), " "))
		}
		return rows.Err()
	}
	if len(args) >= 4 && len(args) <= 5 && args[0] == "message" {
		if !validID(args[1]) {
			return fmt.Errorf("invalid message ID")
		}
		status := args[2]
		if status != "open" && status != "accepted" && status != "done" && status != "hidden" {
			return fmt.Errorf("invalid feedback status")
		}
		reply, err := guitarText(args[3], 0, 2000)
		if err != nil {
			return err
		}
		if len(args) != 4 {
			return fmt.Errorf("usage: guitar-review message <id> <status> <reply>")
		}
		return moderateGuitarMessage(ctx, db, args[1], status, reply, false)
	}
	if (len(args) == 3 || len(args) == 4) && args[0] == "export" && validID(args[1]) {
		page := 1
		if len(args) == 4 {
			var err error
			page, err = strconv.Atoi(args[3])
			if err != nil || page < 1 || page > 12 {
				return fmt.Errorf("page must be 1–12")
			}
		}
		var data []byte
		if err := db.QueryRow(ctx, `SELECT f.bytes FROM guitar_uploads u
 JOIN guitar_score_files f ON (f.app_id,f.score_id)=(u.app_id,u.score_id)
 WHERE u.app_id='guitar' AND u.id=$1 AND f.file_no=$2
 AND f.original_upload_id IS NOT DISTINCT FROM (SELECT original_upload_id FROM guitar_score_files
  WHERE app_id=u.app_id AND original_upload_id=u.id LIMIT 1)`, args[1], page).Scan(&data); err != nil {
			return err
		}
		return os.WriteFile(args[2], data, 0600)
	}
	if len(args) == 1 && args[0] == "list" {
		rows, e := db.Query(ctx, `SELECT u.id::text,s.title,(SELECT count(*) FROM guitar_score_files f WHERE `+guitarCanonicalFile+`)
 FROM guitar_uploads u JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)
 WHERE u.app_id='guitar' AND u.status='pending' ORDER BY u.created_at LIMIT 100`)
		if e != nil {
			return e
		}
		defer rows.Close()
		for rows.Next() {
			var id, title string
			var pages int
			if e = rows.Scan(&id, &title, &pages); e != nil {
				return e
			}
			fmt.Printf("%s  %s  (%d file pages; PDFs may contain more)\n", id, title, pages)
		}
		return rows.Err()
	}
	if len(args) < 2 || len(args) > 3 || !validID(args[1]) {
		return fmt.Errorf("usage: guitar-review list | messages | message <id> <status> <reply> | approve <upload-id> | reject <upload-id> [note]")
	}
	switch args[0] {
	case "approve":
		if len(args) != 2 {
			return fmt.Errorf("approve takes only an upload ID")
		}
		return reviewGuitarUpload(ctx, db, args[1], "approve", "")
	case "reject":
		note := guitarDefaultReviewNote
		if len(args) == 3 {
			var err error
			note, err = guitarText(args[2], 1, 300)
			if err != nil {
				return err
			}
		}
		return reviewGuitarUpload(ctx, db, args[1], "reject", note)
	default:
		return fmt.Errorf("unknown moderation action")
	}
}

const guitarDefaultReviewNote = "请检查曲谱清晰度与分享权限后重新提交。"

func reviewGuitarUpload(ctx context.Context, db *pgxpool.Pool, uploadID, decision, note string) error {
	tx, e := db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	var id, status string
	e = tx.QueryRow(ctx, `SELECT score_id,status FROM guitar_uploads WHERE app_id='guitar' AND id=$1 FOR UPDATE`, uploadID).Scan(&id, &status)
	if e != nil {
		return e
	}
	if status != "pending" {
		return fail(409, "upload_reviewed", "这份曲谱已经审核，请刷新列表。")
	}
	switch decision {
	case "approve":
		tag, err := tx.Exec(ctx, `UPDATE guitar_scores s SET status='published'
 WHERE s.app_id='guitar' AND s.id=$1 AND s.status='private'
 AND EXISTS(SELECT 1 FROM guitar_score_files f WHERE `+guitarCanonicalFile+` AND f.file_no=1)`, id)
		if err != nil {
			return err
		}
		if tag.RowsAffected() != 1 {
			return fail(409, "upload_unavailable", "曲谱或谱面已不可用。")
		}
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='approved',reviewed_at=now() WHERE app_id='guitar' AND id=$1`, uploadID)
		if e == nil {
			_, e = tx.Exec(ctx, `INSERT INTO guitar_request_answers(request_id,score_id,responder_id)
 SELECT m.id,$1,u.user_id FROM guitar_uploads u JOIN guitar_messages m ON m.app_id=u.app_id AND m.id=u.request_id
 WHERE u.app_id='guitar' AND u.id=$2 AND m.requested_title IS NOT NULL AND m.status<>'hidden'
 ON CONFLICT DO NOTHING`, id, uploadID)
		}
	case "reject":
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='rejected',review_note=$2,reviewed_at=now() WHERE app_id='guitar' AND id=$1`, uploadID, note)
	default:
		return fmt.Errorf("unknown moderation action")
	}
	if e != nil {
		return e
	}
	return tx.Commit(ctx)
}
