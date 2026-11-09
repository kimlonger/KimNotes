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

func (a *App) guitarScore(w http.ResponseWriter, r *http.Request) error {
	var id, title, artist, genre, desc, category, difficulty, credit string
	var mt *string
	var notation json.RawMessage
	var courseAvailable bool
	var stars float64
	var attributes guitarScoreAttributes
	e := a.db.QueryRow(r.Context(), `SELECT s.id,s.title,s.artist,s.genre,s.description,s.category,s.difficulty,s.credit,s.notation,s.mime,
 EXISTS(SELECT 1 FROM guitar_courses c WHERE c.app_id=s.app_id AND c.id=s.notation->>'courseId' AND c.status='published')
 ,`+guitarStarsSQL+`,s.attributes FROM guitar_scores s WHERE s.app_id=$1 AND s.id=$2 AND s.status='published'`, guitarAppID, r.PathValue("score")).Scan(&id, &title, &artist, &genre, &desc, &category, &difficulty, &credit, &notation, &mt, &courseAvailable, &stars, &attributes)
	if e != nil {
		return e
	}
	pages, err := a.guitarScorePages(r, id, mt)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"id": id, "title": title, "artist": artist, "genre": genre, "description": desc, "category": category, "difficulty": difficulty, "difficultyStars": stars, "credit": credit, "notation": notation, "mime": mt, "courseAvailable": courseAvailable, "pages": pages, "key": attributes.Key, "capo": attributes.Capo, "meter": attributes.Meter, "bpm": attributes.BPM, "chords": attributes.Chords})
	return nil
}
func (a *App) guitarScoreFile(w http.ResponseWriter, r *http.Request) error {
	var mt, title string
	var data []byte
	e := a.db.QueryRow(r.Context(), `SELECT mime,title,bytes FROM guitar_scores WHERE app_id=$1 AND id=$2 AND status='published' AND bytes IS NOT NULL`, guitarAppID, r.PathValue("score")).Scan(&mt, &title, &data)
	if e != nil {
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
	rows, e := a.db.Query(r.Context(), `SELECT id::text,title,status,review_note,published_score_id,created_at`+mine+` ORDER BY created_at DESC,id LIMIT $3 OFFSET $4`, p.AppID, p.UserID, size, (page-1)*size)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, status, note string
		var score *string
		var at time.Time
		if e = rows.Scan(&id, &title, &status, &note, &score, &at); e != nil {
			return e
		}
		out = append(out, map[string]any{"id": id, "title": title, "status": status, "reviewNote": note, "publishedScoreId": score, "createdAt": at})
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
	artist, genre, e := guitarScoreMetadata(r.FormValue("artist"), r.FormValue("genre"))
	if e != nil {
		return e
	}
	category := r.FormValue("category")
	if category != "弹唱" && category != "指弹" && category != "练习" {
		return fail(400, "invalid_category", "曲谱分类无效。")
	}
	stars, difficulty, e := guitarUploadDifficulty(r.FormValue("difficultyStars"), r.FormValue("difficulty"))
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
	_, e = tx.Exec(r.Context(), `INSERT INTO guitar_uploads(id,user_id,title,description,category,difficulty,filename,mime,bytes,artist,genre,permission_confirmed_at,difficulty_stars,request_id,attributes) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,now(),$12,$13,$14)`, id, p.UserID, title, desc, category, difficulty, filename, mt, data, artist, genre, stars, requestID, attributes)
	if e != nil {
		return e
	}
	for _, page := range extra {
		if _, e = tx.Exec(r.Context(), `INSERT INTO guitar_upload_pages(upload_id,page,mime,bytes) VALUES($1,$2,$3,$4)`, id, page.page, page.mime, page.data); e != nil {
			return e
		}
	}
	if e = tx.Commit(r.Context()); e != nil {
		return e
	}
	writeJSON(w, 201, map[string]any{"id": id, "status": "pending"})
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

// Moderation is an authenticated operator CLI using database credentials, not a public admin endpoint.
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
		tag, err := db.Exec(ctx, `UPDATE guitar_messages SET status=$2,reply=$3,updated_at=now()
 WHERE app_id='guitar' AND id=$1 AND ((requested_title IS NULL AND question_title IS NULL) OR $2='hidden')`, args[1], status, reply)
		if err != nil {
			return err
		}
		if tag.RowsAffected() == 0 {
			return fmt.Errorf("message not found")
		}
		return nil
	}
	if (len(args) == 3 || len(args) == 4) && args[0] == "export" && validID(args[1]) {
		var data []byte
		if len(args) == 4 {
			page, err := strconv.Atoi(args[3])
			if err != nil || page < 1 || page > 12 {
				return fmt.Errorf("page must be 1–12")
			}
			if page == 1 {
				if err = db.QueryRow(ctx, `SELECT bytes FROM guitar_uploads WHERE app_id='guitar' AND id=$1`, args[1]).Scan(&data); err != nil {
					return err
				}
			} else {
				if err = db.QueryRow(ctx, `SELECT bytes FROM guitar_upload_pages WHERE app_id='guitar' AND upload_id=$1 AND page=$2`, args[1], page).Scan(&data); err != nil {
					return err
				}
			}
		} else {
			if err := db.QueryRow(ctx, `SELECT bytes FROM guitar_uploads WHERE app_id='guitar' AND id=$1`, args[1]).Scan(&data); err != nil {
				return err
			}
		}
		return os.WriteFile(args[2], data, 0600)
	}
	if len(args) == 1 && args[0] == "list" {
		rows, e := db.Query(ctx, `SELECT u.id::text,u.title,1+(SELECT count(*) FROM guitar_upload_pages p WHERE p.app_id=u.app_id AND p.upload_id=u.id) FROM guitar_uploads u WHERE u.app_id='guitar' AND u.status='pending' ORDER BY u.created_at LIMIT 100`)
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
	tx, e := db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	var title, artist, genre, desc, category, difficulty, mt, owner, status string
	var stars float64
	var data []byte
	var attributes json.RawMessage
	e = tx.QueryRow(ctx, `SELECT title,artist,genre,description,category,difficulty,mime,bytes,user_id::text,status,COALESCE(difficulty_stars,CASE difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END)::double precision,attributes FROM guitar_uploads WHERE app_id='guitar' AND id=$1 FOR UPDATE`, args[1]).Scan(&title, &artist, &genre, &desc, &category, &difficulty, &mt, &data, &owner, &status, &stars, &attributes)
	if e != nil {
		return e
	}
	if status != "pending" {
		return fmt.Errorf("upload has already been reviewed")
	}
	switch args[0] {
	case "approve":
		if len(args) != 2 {
			return fmt.Errorf("approve takes only an upload ID")
		}
		id := "upload-" + args[1]
		credit := "琴友 " + strings.ReplaceAll(owner, "-", "")[:6]
		if _, e = tx.Exec(ctx, `INSERT INTO guitar_scores(id,title,description,category,difficulty,mime,bytes,credit,artist,genre,difficulty_stars,attributes) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)`, id, title, desc, category, difficulty, mt, data, credit, artist, genre, stars, attributes); e != nil {
			return e
		}
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='approved',published_score_id=$2,reviewed_at=now() WHERE app_id='guitar' AND id=$1`, args[1], id)
		if e == nil {
			_, e = tx.Exec(ctx, `INSERT INTO guitar_score_pages(score_id,page,mime,bytes) SELECT $1,page,mime,bytes FROM guitar_upload_pages WHERE app_id='guitar' AND upload_id=$2`, id, args[1])
		}
		if e == nil {
			_, e = tx.Exec(ctx, `INSERT INTO guitar_request_answers(request_id,score_id,responder_id)
 SELECT m.id,$1,u.user_id FROM guitar_uploads u JOIN guitar_messages m ON m.app_id=u.app_id AND m.id=u.request_id
 WHERE u.app_id='guitar' AND u.id=$2 AND m.requested_title IS NOT NULL AND m.status<>'hidden'
 ON CONFLICT DO NOTHING`, id, args[1])
		}
	case "reject":
		note := "请检查曲谱清晰度与分享权限后重新提交。"
		if len(args) == 3 {
			note, e = guitarText(args[2], 1, 300)
			if e != nil {
				return e
			}
		}
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='rejected',review_note=$2,reviewed_at=now() WHERE app_id='guitar' AND id=$1`, args[1], note)
	default:
		return fmt.Errorf("unknown moderation action")
	}
	if e != nil {
		return e
	}
	return tx.Commit(ctx)
}
