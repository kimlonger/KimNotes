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
	"strings"
	"time"
)

func (a *App) guitarScores(w http.ResponseWriter, r *http.Request) error {
	rows, e := a.db.Query(r.Context(), `SELECT id,title,description,category,difficulty,credit,mime,notation FROM guitar_scores WHERE app_id=$1 AND status='published' ORDER BY created_at,id LIMIT 300`, guitarAppID)
	if e != nil {
		return e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, desc, category, difficulty, credit string
		var mt *string
		var notation json.RawMessage
		if e = rows.Scan(&id, &title, &desc, &category, &difficulty, &credit, &mt, &notation); e != nil {
			return e
		}
		var preview map[string]any
		if len(notation) > 0 {
			if e = json.Unmarshal(notation, &preview); e != nil {
				return e
			}
			if bars, ok := preview["bars"].([]any); ok {
				preview["totalBars"] = len(bars)
				if len(bars) > 2 {
					preview["bars"] = bars[:2]
				}
			}
			delete(preview, "phrases")
		}
		out = append(out, map[string]any{"id": id, "title": title, "description": desc, "category": category, "difficulty": difficulty, "credit": credit, "mime": mt, "preview": preview})
	}
	if e = rows.Err(); e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"scores": out})
	return nil
}
func (a *App) guitarScore(w http.ResponseWriter, r *http.Request) error {
	var id, title, desc, category, difficulty, credit string
	var mt *string
	var notation json.RawMessage
	e := a.db.QueryRow(r.Context(), `SELECT id,title,description,category,difficulty,credit,notation,mime FROM guitar_scores WHERE app_id=$1 AND id=$2 AND status='published'`, guitarAppID, r.PathValue("score")).Scan(&id, &title, &desc, &category, &difficulty, &credit, &notation, &mt)
	if e != nil {
		return e
	}
	writeJSON(w, 200, map[string]any{"id": id, "title": title, "description": desc, "category": category, "difficulty": difficulty, "credit": credit, "notation": notation, "mime": mt})
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
	p := identity(r)
	rows, e := a.db.Query(r.Context(), `SELECT id::text,title,status,review_note,published_score_id,created_at FROM guitar_uploads WHERE app_id=$1 AND user_id=$2 ORDER BY created_at DESC LIMIT 200`, p.AppID, p.UserID)
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
	writeJSON(w, 200, map[string]any{"uploads": out})
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
	category := r.FormValue("category")
	if category != "弹唱" && category != "指弹" && category != "练习" {
		return fail(400, "invalid_category", "曲谱分类无效。")
	}
	difficulty := r.FormValue("difficulty")
	if difficulty != "入门" && difficulty != "初级" && difficulty != "中级" {
		return fail(400, "invalid_difficulty", "难度无效。")
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
	id := newID()
	filename := path.Base(strings.ReplaceAll(header.Filename, "\\", "/"))
	if len(filename) > 240 {
		filename = "score"
	}
	_, e = a.db.Exec(r.Context(), `INSERT INTO guitar_uploads(id,user_id,title,description,category,difficulty,filename,mime,bytes) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)`, id, p.UserID, title, desc, category, difficulty, filename, mt, data)
	if e != nil {
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
		tag, err := db.Exec(ctx, `UPDATE guitar_messages SET status=$2,reply=$3 WHERE app_id='guitar' AND id=$1`, args[1], status, reply)
		if err != nil {
			return err
		}
		if tag.RowsAffected() == 0 {
			return fmt.Errorf("message not found")
		}
		return nil
	}
	if len(args) == 3 && args[0] == "export" && validID(args[1]) {
		var data []byte
		if err := db.QueryRow(ctx, `SELECT bytes FROM guitar_uploads WHERE app_id='guitar' AND id=$1`, args[1]).Scan(&data); err != nil {
			return err
		}
		return os.WriteFile(args[2], data, 0600)
	}
	if len(args) == 1 && args[0] == "list" {
		rows, e := db.Query(ctx, `SELECT id::text,title FROM guitar_uploads WHERE app_id='guitar' AND status='pending' ORDER BY created_at LIMIT 100`)
		if e != nil {
			return e
		}
		defer rows.Close()
		for rows.Next() {
			var id, title string
			if e = rows.Scan(&id, &title); e != nil {
				return e
			}
			fmt.Printf("%s  %s\n", id, title)
		}
		return rows.Err()
	}
	if len(args) < 2 || len(args) > 3 || !validID(args[1]) {
		return fmt.Errorf("usage: guitar-review list | approve <upload-id> | reject <upload-id> [note]")
	}
	tx, e := db.Begin(ctx)
	if e != nil {
		return e
	}
	defer tx.Rollback(ctx)
	var title, desc, category, difficulty, mt, owner, status string
	var data []byte
	e = tx.QueryRow(ctx, `SELECT title,description,category,difficulty,mime,bytes,user_id::text,status FROM guitar_uploads WHERE app_id='guitar' AND id=$1 FOR UPDATE`, args[1]).Scan(&title, &desc, &category, &difficulty, &mt, &data, &owner, &status)
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
		if _, e = tx.Exec(ctx, `INSERT INTO guitar_scores(id,title,description,category,difficulty,mime,bytes,credit) VALUES($1,$2,$3,$4,$5,$6,$7,$8)`, id, title, desc, category, difficulty, mt, data, credit); e != nil {
			return e
		}
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='approved',published_score_id=$2 WHERE app_id='guitar' AND id=$1`, args[1], id)
	case "reject":
		note := "请检查曲谱清晰度与分享权限后重新提交。"
		if len(args) == 3 {
			note, e = guitarText(args[2], 1, 300)
			if e != nil {
				return e
			}
		}
		_, e = tx.Exec(ctx, `UPDATE guitar_uploads SET status='rejected',review_note=$2 WHERE app_id='guitar' AND id=$1`, args[1], note)
	default:
		return fmt.Errorf("unknown moderation action")
	}
	if e != nil {
		return e
	}
	return tx.Commit(ctx)
}
