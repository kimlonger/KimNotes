package main

import (
	"archive/zip"
	"bytes"
	"fmt"
	"mime"
	"net/http"
	"strconv"
	"strings"
)

func (a *App) guitarScoreDownload(w http.ResponseWriter, r *http.Request) error {
	var mt, title string
	var data []byte
	if err := a.db.QueryRow(r.Context(), `SELECT mime,title,bytes FROM guitar_scores WHERE app_id=$1 AND id=$2 AND status='published' AND bytes IS NOT NULL`, guitarAppID, r.PathValue("score")).Scan(&mt, &title, &data); err != nil {
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
		rows, err := a.db.Query(r.Context(), `SELECT p.page,p.mime,p.bytes FROM guitar_score_pages p JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id WHERE s.app_id=$1 AND s.id=$2 AND s.status='published' ORDER BY p.page`, guitarAppID, r.PathValue("score"))
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
	w.Header().Set("Content-Type", mt)
	w.Header().Set("Content-Disposition", mime.FormatMediaType("attachment", map[string]string{"filename": filename}))
	w.Header().Set("Content-Length", strconv.Itoa(len(data)))
	w.Header().Set("Content-Security-Policy", "sandbox; default-src 'none'")
	w.WriteHeader(http.StatusOK)
	_, err := w.Write(data)
	return err
}
