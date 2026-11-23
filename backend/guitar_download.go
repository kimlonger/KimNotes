package main

import (
	"archive/zip"
	"bytes"
	"fmt"
	"mime"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func (a *App) guitarScoreDownload(w http.ResponseWriter, r *http.Request) error {
	ctx, p := r.Context(), identity(r)
	// Android versions that already use bearer sessions retain their GET route.
	// Cookie-based downloads must use the same-origin POST to avoid cross-site debits.
	if r.Method != http.MethodPost && !(r.Method == http.MethodGet && p.ClientKind == "desktop") {
		w.Header().Set("Allow", "POST")
		return fail(405, "download_post_required", "请从看谱页的下载按钮下载曲谱。")
	}
	if err := a.limited(r, "guitar:export:"+p.UserID, 30, time.Minute); err != nil {
		return err
	}
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if err = lockAccountPoints(ctx, tx, p.UserID); err != nil {
		return err
	}
	v, err := accountPointsState(ctx, tx, guitarAppID, p.UserID, time.Now())
	if err != nil {
		return err
	}
	quote, err := guitarDownloadDetails(ctx, tx, v, r.PathValue("score"))
	if err != nil {
		return err
	}
	if err = quote.allow(); err != nil {
		return err
	}
	var mt, title string
	var data []byte
	if err := tx.QueryRow(ctx, `SELECT f.mime,s.title,f.bytes FROM guitar_scores s
 JOIN guitar_score_files f ON `+guitarCanonicalFile+` AND f.file_no=1
 WHERE s.app_id=$1 AND s.id=$2 AND s.status='published'`, guitarAppID, quote.ScoreID).Scan(&mt, &title, &data); err != nil {
		return err
	}
	base := strings.Trim(strings.Map(func(c rune) rune {
		if c < 32 || strings.ContainsRune(`/\:*?"<>|`, c) {
			return -1
		}
		return c
	}, title), ". ")
	if base == "" {
		base = "曲谱"
	}
	ext := map[string]string{"image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp", "application/pdf": ".pdf"}
	filename := base + ext[mt]
	if strings.HasPrefix(mt, "image/") {
		rows, err := tx.Query(ctx, `SELECT f.file_no,f.mime,f.bytes FROM guitar_scores s
 JOIN guitar_score_files f ON `+guitarCanonicalFile+` AND f.file_no>1
 WHERE s.app_id=$1 AND s.id=$2 AND s.status='published' ORDER BY f.file_no`, guitarAppID, quote.ScoreID)
		if err != nil {
			return err
		}
		defer rows.Close()
		type page struct {
			number int
			mt     string
			data   []byte
		}
		pages := []page{{1, mt, data}}
		for rows.Next() {
			var p page
			if err = rows.Scan(&p.number, &p.mt, &p.data); err != nil {
				return err
			}
			pages = append(pages, p)
		}
		if err = rows.Err(); err != nil {
			return err
		}
		if len(pages) > 1 {
			var out bytes.Buffer
			archive := zip.NewWriter(&out)
			for _, p := range pages {
				f, err := archive.CreateHeader(&zip.FileHeader{Name: fmt.Sprintf("%s-%02d%s", base, p.number, ext[p.mt]), Method: zip.Store})
				if err != nil {
					return err
				}
				if _, err = f.Write(p.data); err != nil {
					return err
				}
			}
			if err = archive.Close(); err != nil {
				return err
			}
			data = out.Bytes()
			mt = "application/zip"
			filename = base + ".zip"
		}
	}
	if err = allowGuitarMedia(ctx, tx, p.UserID, guitarClientIP(r), len(data)); err != nil {
		return err
	}
	if err = recordGuitarDownload(ctx, tx, p.UserID, quote); err != nil {
		return err
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	w.Header().Set("Content-Type", mt)
	w.Header().Set("Content-Disposition", mime.FormatMediaType("attachment", map[string]string{"filename": filename}))
	w.Header().Set("Content-Length", strconv.Itoa(len(data)))
	w.Header().Set("Content-Security-Policy", "sandbox; default-src 'none'")
	w.WriteHeader(http.StatusOK)
	_, err = w.Write(data)
	return err
}
