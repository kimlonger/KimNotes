package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

const maxCatalogBytes = 1 << 20

var contentVersionPattern = regexp.MustCompile(`^[A-Za-z0-9._:-]{1,128}$`)
var contentDatePattern = regexp.MustCompile(`^[0-9]{4}-[0-9]{2}-[0-9]{2}$`)

func validContentDate(value string) bool {
	_, err := time.Parse(time.DateOnly, value)
	return err == nil && contentDatePattern.MatchString(value)
}

type catalogDocument struct {
	Projects []json.RawMessage `json:"projects"`
	Articles []json.RawMessage `json:"articles"`
	Updates  []json.RawMessage `json:"updates"`
	Version  string            `json:"version,omitempty"`
}

func contentWorkKind(value string) string {
	switch value {
	case "应用":
		return "app"
	case "插件":
		return "plugin"
	case "开源工具", "开源":
		return "tool"
	}
	return ""
}

func contentText(obj map[string]any, key string, required bool, limit int) (string, error) {
	value, exists := obj[key]
	if !exists && !required {
		return "", nil
	}
	s, ok := value.(string)
	if !ok || !validContentText(s, limit) {
		return "", fmt.Errorf("invalid public content field %s", key)
	}
	return s, nil
}

func validContentText(s string, limit int) bool {
	if strings.TrimSpace(s) == "" || len(utf16.Encode([]rune(s))) > limit {
		return false
	}
	for _, r := range s {
		if r < 0x20 && r != '\t' && r != '\n' && r != '\r' || r == 0x7f {
			return false
		}
	}
	return true
}

func contentFields(obj map[string]any, fields string) error {
	allowed := map[string]bool{}
	for _, key := range strings.Fields(fields) {
		allowed[key] = true
	}
	for key := range obj {
		if !allowed[key] {
			return fmt.Errorf("unknown public content field %s", key)
		}
	}
	return nil
}

func contentStrings(obj map[string]any, key string, required bool) error {
	v, exists := obj[key]
	if !exists && !required {
		return nil
	}
	list, ok := v.([]any)
	if !ok || len(list) > 64 {
		return fmt.Errorf("invalid content list %s", key)
	}
	for _, value := range list {
		text, ok := value.(string)
		if !ok || !validContentText(text, 2000) {
			return fmt.Errorf("invalid content list item %s", key)
		}
	}
	return nil
}

func validContentURL(value string) bool {
	u, err := url.Parse(value)
	if err != nil || len(value) > 2048 || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.Opaque != "" {
		return false
	}
	for _, r := range value {
		if unicode.IsSpace(r) || r == '\ufeff' || r == '\\' || r <= 0x1f || r == 0x7f {
			return false
		}
	}
	return true
}

func contentURL(obj map[string]any, key string, required bool) error {
	value, exists := obj[key]
	if (!exists || value == nil) && !required {
		return nil
	}
	s, ok := value.(string)
	if !ok || !validContentURL(s) {
		return fmt.Errorf("invalid HTTPS content URL %s", key)
	}
	return nil
}

func contentSources(obj map[string]any) error {
	items, ok := obj["sources"].([]any)
	if !ok || len(items) > 64 {
		return fmt.Errorf("invalid content sources")
	}
	for _, item := range items {
		source, ok := item.(map[string]any)
		if !ok || contentFields(source, "title url") != nil || contentURL(source, "url", true) != nil {
			return fmt.Errorf("invalid content source")
		}
		if _, err := contentText(source, "title", true, 300); err != nil {
			return err
		}
	}
	return nil
}

func contentRef(obj map[string]any, key string, required bool) (string, error) {
	v, exists := obj[key]
	if (!exists || v == nil) && !required {
		return "", nil
	}
	id, ok := v.(string)
	if !ok || !projectIDPattern.MatchString(id) {
		return "", fmt.Errorf("invalid content reference %s", key)
	}
	return id, nil
}

func contentObject(raw json.RawMessage, kind string) (map[string]any, error) {
	var obj map[string]any
	if len(raw) > 128<<10 || json.Unmarshal(raw, &obj) != nil || obj == nil {
		return nil, fmt.Errorf("invalid %s payload", kind)
	}
	if _, err := contentRef(obj, "id", true); err != nil {
		return nil, err
	}
	fields := "id title description sources"
	if kind == "projects" {
		fields = "id name type platform icon tag description long url action source download license requirements features quickStart topics sources release syncScope"
	} else if kind == "articles" {
		fields += " category project sourceNote body"
	} else if kind == "updates" {
		fields += " project label date version article"
	} else {
		return nil, fmt.Errorf("invalid content kind")
	}
	if err := contentFields(obj, fields); err != nil {
		return nil, err
	}
	if err := contentSources(obj); err != nil {
		return nil, err
	}
	if _, err := contentText(obj, "description", true, 2000); err != nil {
		return nil, err
	}
	if kind != "projects" {
		if _, err := contentText(obj, "title", true, 300); err != nil {
			return nil, err
		}
		if _, err := contentRef(obj, "project", kind == "updates"); err != nil {
			return nil, err
		}
	}
	if kind == "projects" {
		for _, key := range []string{"name", "type", "platform", "action"} {
			if _, err := contentText(obj, key, true, 160); err != nil {
				return nil, err
			}
		}
		if _, err := contentRef(obj, "icon", true); err != nil {
			return nil, err
		}
		if _, err := contentText(obj, "tag", false, 160); err != nil {
			return nil, err
		}
		if _, err := contentText(obj, "long", true, 12000); err != nil {
			return nil, err
		}
		if contentWorkKind(obj["type"].(string)) == "" {
			return nil, fmt.Errorf("invalid work kind")
		}
		if _, err := contentText(obj, "license", false, 160); err != nil {
			return nil, err
		}
		for _, key := range []string{"url", "source", "download"} {
			if _, exists := obj[key]; !exists {
				return nil, fmt.Errorf("missing nullable URL field %s", key)
			}
			if err := contentURL(obj, key, false); err != nil {
				return nil, err
			}
		}
		for _, key := range []string{"features", "quickStart", "topics", "requirements"} {
			if err := contentStrings(obj, key, key != "requirements"); err != nil {
				return nil, err
			}
		}
		if value, exists := obj["syncScope"]; exists {
			scope, ok := value.(map[string]any)
			if !ok || contentFields(scope, "included localOnly web") != nil {
				return nil, fmt.Errorf("invalid synchronization scope")
			}
			for _, key := range []string{"included", "localOnly", "web"} {
				if err := contentStrings(scope, key, true); err != nil {
					return nil, err
				}
			}
		}
		if value, exists := obj["release"]; exists {
			release, ok := value.(map[string]any)
			if !ok || contentFields(release, "version date fileName bytes sha256 url notesUrl") != nil || contentURL(release, "url", true) != nil || contentURL(release, "notesUrl", true) != nil {
				return nil, fmt.Errorf("invalid work release")
			}
			for key, limit := range map[string]int{"version": 64, "date": 10, "fileName": 255, "sha256": 64} {
				if _, err := contentText(release, key, true, limit); err != nil {
					return nil, err
				}
			}
			n, ok := release["bytes"].(float64)
			_, checksumError := hex.DecodeString(release["sha256"].(string))
			if !ok || n < 0 || n != math.Trunc(n) || n > (1<<53)-1 || len(release["sha256"].(string)) != 64 || checksumError != nil || strings.ContainsAny(release["fileName"].(string), "\\/") {
				return nil, fmt.Errorf("invalid release size or checksum")
			}
			if !validContentDate(release["date"].(string)) {
				return nil, fmt.Errorf("invalid release date")
			}
		}
	} else if kind == "articles" {
		for key, limit := range map[string]int{"category": 160, "sourceNote": 2000} {
			if _, err := contentText(obj, key, key == "category", limit); err != nil {
				return nil, err
			}
		}
		body, ok := obj["body"].([]any)
		if !ok || len(body) == 0 || len(body) > 64 {
			return nil, fmt.Errorf("invalid article body")
		}
		for _, part := range body {
			pair, ok := part.([]any)
			if !ok || len(pair) != 2 {
				return nil, fmt.Errorf("invalid article paragraph")
			}
			for i, item := range pair {
				s, ok := item.(string)
				limit := 12000
				if i == 0 {
					limit = 300
				}
				if !ok || !validContentText(s, limit) {
					return nil, fmt.Errorf("invalid article paragraph text")
				}
			}
		}
	} else {
		for key, limit := range map[string]int{"label": 160, "date": 10, "version": 64} {
			if _, err := contentText(obj, key, key == "label", limit); err != nil {
				return nil, err
			}
		}
		if date, _ := obj["date"].(string); date != "" {
			if !validContentDate(date) {
				return nil, fmt.Errorf("invalid update date")
			}
		}
		if _, err := contentRef(obj, "article", false); err != nil {
			return nil, err
		}
	}
	return obj, nil
}

func validateContent(doc catalogDocument) error {
	if doc.Version != "" && !contentVersionPattern.MatchString(doc.Version) {
		return fmt.Errorf("invalid content version")
	}
	if doc.Projects == nil || doc.Articles == nil || doc.Updates == nil || len(doc.Projects) > 256 || len(doc.Articles) > 500 || len(doc.Updates) > 500 {
		return fmt.Errorf("invalid catalogue collections")
	}
	works, articles := map[string]bool{}, map[string]bool{}
	for _, group := range []struct {
		kind  string
		items []json.RawMessage
	}{{"projects", doc.Projects}, {"articles", doc.Articles}, {"updates", doc.Updates}} {
		seen := map[string]bool{}
		for _, raw := range group.items {
			obj, err := contentObject(raw, group.kind)
			if err != nil {
				return err
			}
			id := obj["id"].(string)
			if seen[id] {
				return fmt.Errorf("duplicate %s ID", group.kind)
			}
			seen[id] = true
			if group.kind == "projects" {
				works[id] = true
			} else {
				project, _ := contentRef(obj, "project", group.kind == "updates")
				if project != "" && !works[project] {
					return fmt.Errorf("unknown content project reference")
				}
				if group.kind == "articles" {
					articles[id] = true
				} else if article, _ := contentRef(obj, "article", false); article != "" && !articles[article] {
					return fmt.Errorf("unknown content article reference")
				}
			}
		}
	}
	return nil
}

func decodeContentSeed(raw []byte) (catalogDocument, error) {
	var doc catalogDocument
	if len(raw) > maxCatalogBytes {
		return doc, fmt.Errorf("catalogue exceeds 1 MiB")
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.DisallowUnknownFields()
	if err := d.Decode(&doc); err != nil {
		return doc, fmt.Errorf("invalid content seed JSON")
	}
	if d.Decode(&struct{}{}) != io.EOF {
		return doc, fmt.Errorf("content seed requires one object")
	}
	return doc, validateContent(doc)
}

type contentImportResult struct{ Projects, Articles, Updates int64 }

func importContent(ctx context.Context, db *pgxpool.Pool, raw []byte, replace bool) (contentImportResult, error) {
	var result contentImportResult
	doc, err := decodeContentSeed(raw)
	if err != nil {
		return result, err
	}
	tx, err := db.Begin(ctx)
	if err != nil {
		return result, err
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, "SELECT pg_advisory_xact_lock(725194139)"); err != nil {
		return result, err
	}
	batch := &pgx.Batch{}
	for i, raw := range doc.Projects {
		obj, _ := contentObject(raw, "projects")
		suffix := " ON CONFLICT(id) DO NOTHING"
		if replace {
			suffix = " ON CONFLICT(id) DO UPDATE SET kind=EXCLUDED.kind,payload=EXCLUDED.payload,status='published',sort_order=EXCLUDED.sort_order,updated_at=clock_timestamp()"
		}
		batch.Queue("INSERT INTO content_works(id,kind,payload,sort_order) VALUES($1,$2,$3,$4)"+suffix, obj["id"], contentWorkKind(obj["type"].(string)), []byte(raw), i)
	}
	for i, raw := range doc.Articles {
		obj, _ := contentObject(raw, "articles")
		project, _ := contentRef(obj, "project", false)
		var reference any
		if project != "" {
			reference = project
		}
		suffix := " ON CONFLICT(id) DO NOTHING"
		if replace {
			suffix = " ON CONFLICT(id) DO UPDATE SET project_id=EXCLUDED.project_id,payload=EXCLUDED.payload,status='published',sort_order=EXCLUDED.sort_order,updated_at=clock_timestamp()"
		}
		batch.Queue("INSERT INTO content_articles(id,project_id,payload,sort_order) VALUES($1,$2,$3,$4)"+suffix, obj["id"], reference, []byte(raw), i)
	}
	for i, raw := range doc.Updates {
		obj, _ := contentObject(raw, "updates")
		article, _ := contentRef(obj, "article", false)
		var reference any
		if article != "" {
			reference = article
		}
		suffix := " ON CONFLICT(id) DO NOTHING"
		if replace {
			suffix = " ON CONFLICT(id) DO UPDATE SET project_id=EXCLUDED.project_id,article_id=EXCLUDED.article_id,payload=EXCLUDED.payload,status='published',sort_order=EXCLUDED.sort_order,updated_at=clock_timestamp()"
		}
		batch.Queue("INSERT INTO content_updates(id,project_id,article_id,payload,sort_order) VALUES($1,$2,$3,$4,$5)"+suffix, obj["id"], obj["project"], reference, []byte(raw), i)
	}
	results := tx.SendBatch(ctx, batch)
	defer results.Close()
	for i := 0; i < len(doc.Projects)+len(doc.Articles)+len(doc.Updates); i++ {
		tag, err := results.Exec()
		if err != nil {
			return result, err
		}
		if i < len(doc.Projects) {
			result.Projects += tag.RowsAffected()
		} else if i < len(doc.Projects)+len(doc.Articles) {
			result.Articles += tag.RowsAffected()
		} else {
			result.Updates += tag.RowsAffected()
		}
	}
	if err = results.Close(); err != nil {
		return result, err
	}
	if err = tx.Commit(ctx); err != nil {
		return result, err
	}
	return result, nil
}

func (a *App) contentCatalog(w http.ResponseWriter, r *http.Request) error {
	tx, err := a.db.BeginTx(r.Context(), pgx.TxOptions{IsoLevel: pgx.RepeatableRead, AccessMode: pgx.ReadOnly})
	if err != nil {
		return err
	}
	defer tx.Rollback(r.Context())
	doc := catalogDocument{Projects: []json.RawMessage{}, Articles: []json.RawMessage{}, Updates: []json.RawMessage{}}
	queries := []string{
		"SELECT payload FROM content_works WHERE status='published' ORDER BY sort_order,id LIMIT 257",
		"SELECT a.payload FROM content_articles a LEFT JOIN content_works w ON w.id=a.project_id WHERE a.status='published' AND (a.project_id IS NULL OR w.status='published') ORDER BY a.sort_order,a.id LIMIT 501",
		"SELECT u.payload FROM content_updates u JOIN content_works w ON w.id=u.project_id LEFT JOIN content_articles a ON a.id=u.article_id LEFT JOIN content_works aw ON aw.id=a.project_id WHERE u.status='published' AND w.status='published' AND (u.article_id IS NULL OR (a.status='published' AND (a.project_id IS NULL OR aw.status='published'))) ORDER BY u.sort_order,u.id LIMIT 501",
	}
	groups := []*[]json.RawMessage{&doc.Projects, &doc.Articles, &doc.Updates}
	used := 0
	for i, query := range queries {
		rows, err := tx.Query(r.Context(), query)
		if err != nil {
			return err
		}
		for rows.Next() {
			var raw []byte
			if err = rows.Scan(&raw); err != nil {
				rows.Close()
				return err
			}
			used += len(raw)
			if used > maxCatalogBytes {
				rows.Close()
				return fail(503, "catalog_unavailable", "内容目录暂时不可用。")
			}
			*groups[i] = append(*groups[i], json.RawMessage(raw))
		}
		err = rows.Err()
		rows.Close()
		if err != nil {
			return err
		}
	}
	if err = validateContent(doc); err != nil {
		return fail(503, "catalog_unavailable", "内容目录暂时不可用。")
	}
	if err = tx.Commit(r.Context()); err != nil {
		return err
	}
	base, err := json.Marshal(doc)
	if err != nil {
		return err
	}
	doc.Version = digest(string(base))
	body, err := json.Marshal(doc)
	if err != nil {
		return err
	}
	if len(body) > maxCatalogBytes {
		return fail(503, "catalog_unavailable", "内容目录暂时不可用。")
	}
	etag := `"` + digest(string(body)) + `"`
	w.Header().Set("ETag", etag)
	w.Header().Set("Cache-Control", "public, max-age=60, must-revalidate")
	for _, candidate := range strings.Split(r.Header.Get("If-None-Match"), ",") {
		candidate = strings.TrimSpace(candidate)
		if candidate == etag || candidate == "W/"+etag || candidate == "*" {
			w.WriteHeader(http.StatusNotModified)
			return nil
		}
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(http.StatusOK)
	_, err = w.Write(body)
	return err
}
