package main

import (
	"bytes"
	"fmt"
	"image"
	"io"
	"mime/multipart"
	"net/http"
	"strconv"
	"strings"
)

type guitarImagePage struct {
	mime string
	data []byte
	page int
}

func guitarExtraPages(headers []*multipart.FileHeader, firstMime string, firstSize int) ([]guitarImagePage, error) {
	pages := []guitarImagePage{}
	if len(headers) > 12 {
		return nil, fail(400, "too_many_pages", "一份曲谱最多12张图片，或1份PDF。")
	}
	if len(headers) > 1 && !strings.HasPrefix(firstMime, "image/") {
		return nil, fail(400, "mixed_pages", "PDF请单独上传，多张图片请按页排序。")
	}
	size := firstSize
	for i, header := range headers[1:] {
		f, err := header.Open()
		if err != nil {
			return nil, err
		}
		data, err := io.ReadAll(io.LimitReader(f, int64((10<<20)-size+1)))
		f.Close()
		if err != nil {
			return nil, err
		}
		size += len(data)
		if len(data) == 0 || size > 10<<20 {
			return nil, fail(400, "invalid_size", "全部文件合计需在10 MiB内。")
		}
		mt := http.DetectContentType(data)
		if mt != "image/png" && mt != "image/jpeg" && mt != "image/webp" {
			return nil, fail(400, "mixed_pages", "多页曲谱只支持图片；PDF请单独上传。")
		}
		config, _, err := image.DecodeConfig(bytes.NewReader(data))
		if err != nil || config.Width < 1 || config.Height < 1 || int64(config.Width)*int64(config.Height) > 40000000 {
			return nil, fail(400, "invalid_image", "图片无效或像素过大。")
		}
		pages = append(pages, guitarImagePage{mt, data, i + 2})
	}
	return pages, nil
}
func (a *App) guitarScorePage(w http.ResponseWriter, r *http.Request) error {
	page, err := strconv.Atoi(r.PathValue("page"))
	if err != nil || page < 2 || page > 12 {
		return fail(404, "not_found", "谱面不存在。")
	}
	var data []byte
	var mime string
	err = a.db.QueryRow(r.Context(), `SELECT p.mime,p.bytes FROM guitar_score_pages p JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id
 WHERE p.app_id='guitar' AND p.score_id=$1 AND p.page=$2 AND s.status='published'
 UNION ALL
 SELECT p.mime,p.bytes FROM guitar_upload_pages p JOIN guitar_uploads u ON u.app_id=p.app_id AND u.id=p.upload_id
 JOIN guitar_scores s ON s.app_id=u.app_id AND s.id='upload-'||u.id::text AND s.status='private'
 WHERE u.app_id='guitar' AND s.id=$1 AND p.page=$2 AND u.user_id=$3 AND u.status IN ('pending','rejected')`, r.PathValue("score"), page, identity(r).UserID).Scan(&mime, &data)
	if err != nil {
		return err
	}
	if err = a.guitarReaderBudget(r, len(data)); err != nil {
		return err
	}
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Content-Security-Policy", "sandbox; default-src 'none'")
	w.WriteHeader(200)
	_, err = w.Write(data)
	return err
}
func guitarScoreImagePage(page int, url string, header []byte) map[string]any {
	result := map[string]any{"page": page, "url": url}
	if config, _, err := image.DecodeConfig(bytes.NewReader(header)); err == nil && config.Width > 0 && config.Height > 0 {
		result["width"] = config.Width
		result["height"] = config.Height
	}
	return result
}
func (a *App) guitarScorePages(r *http.Request, id string, mt *string, firstPageHeader []byte) ([]map[string]any, error) {
	pages := []map[string]any{}
	if mt == nil || !strings.HasPrefix(*mt, "image/") {
		return pages, nil
	}
	pages = append(pages, guitarScoreImagePage(1, "/api/v1/apps/guitar/scores/"+id+"/file", firstPageHeader))
	rows, err := a.db.Query(r.Context(), `SELECT p.page,substring(p.bytes from 1 for 65536) FROM guitar_score_pages p
 JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id
 WHERE p.app_id='guitar' AND p.score_id=$1 AND s.status='published'
 UNION ALL
 SELECT p.page,substring(p.bytes from 1 for 65536) FROM guitar_upload_pages p
 JOIN guitar_uploads u ON u.app_id=p.app_id AND u.id=p.upload_id
 JOIN guitar_scores s ON s.app_id=u.app_id AND s.id='upload-'||u.id::text AND s.status='private'
 WHERE u.app_id='guitar' AND s.id=$1 AND u.user_id=$2 AND u.status IN ('pending','rejected')
 ORDER BY page`, id, identity(r).UserID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var page int
		var header []byte
		if err = rows.Scan(&page, &header); err != nil {
			return nil, err
		}
		pages = append(pages, guitarScoreImagePage(page, fmt.Sprintf("/api/v1/apps/guitar/scores/%s/pages/%d", id, page), header))
	}
	return pages, rows.Err()
}
