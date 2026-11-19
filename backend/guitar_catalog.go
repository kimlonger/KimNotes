package main

import (
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

var guitarCatalogKey = guitarCatalogKeyFrom(`s.description`)
var guitarCatalogBPM = guitarCatalogBPMFrom(`s.description`)

func guitarCatalogKeyFrom(description string) string {
	return `COALESCE(s.attributes->>'key',NULLIF(s.notation->>'key',''),substring(` + description + ` from '([A-G][#b]?)调'),'')`
}

func guitarCatalogBPMFrom(description string) string {
	return `COALESCE((s.attributes->>'bpm')::int,CASE WHEN s.notation->>'bpm' ~ '^[0-9]{1,3}$' THEN (s.notation->>'bpm')::int END,
 substring(` + description + ` from '([0-9]{2,3})[[:space:]]*[Bb][Pp][Mm]')::int)`
}

func catalogOptions(values []string, field, message string, allowed ...string) ([]string, error) {
	selected := []string{}
	for _, value := range values {
		if value == "" {
			continue
		}
		if !slices.Contains(allowed, value) {
			return nil, fail(400, "invalid_"+field, message)
		}
		if !slices.Contains(selected, value) {
			selected = append(selected, value)
		}
	}
	return selected, nil
}

func guitarCatalogNumber(r *http.Request, name string, fallback, max int) (int, error) {
	value := r.URL.Query().Get(name)
	if value == "" {
		return fallback, nil
	}
	n, err := strconv.Atoi(value)
	if err != nil || n < 1 || n > max {
		return 0, fail(400, "invalid_pagination", "分页参数无效。")
	}
	return n, nil
}

func (a *App) guitarScores(w http.ResponseWriter, r *http.Request) error {
	params := r.URL.Query()
	query := strings.TrimSpace(params.Get("q"))
	difficulty := params.Get("difficulty")
	artistID := params.Get("artistId")
	category, err := catalogOptions(params["category"], "category", "曲谱分类无效。", "弹唱", "指弹", "练习")
	if err != nil {
		return err
	}
	key, err := catalogOptions(params["key"], "key", "调式无效。", "C", "G", "D", "A", "E", "F")
	if err != nil {
		return err
	}
	level, err := catalogOptions(params["level"], "level", "难度无效。", "beginner", "intermediate", "advanced")
	if err != nil {
		return err
	}
	order := `s.created_at DESC,s.id`
	switch r.URL.Query().Get("sort") {
	case "", "latest":
	case "popular":
		order = `(SELECT count(DISTINCT p.user_id) FROM guitar_practice_sessions p WHERE p.app_id=s.app_id AND p.score_id=s.id
 AND p.ended_at<=now() AND p.seconds>0 AND p.started_at<=now()
 AND p.started_at>=date_trunc('week',now() AT TIME ZONE 'Asia/Shanghai') AT TIME ZONE 'Asia/Shanghai') DESC,s.created_at DESC,s.id`
	case "bpm":
		order = guitarCatalogBPM + ` ASC NULLS LAST,s.created_at DESC,s.id`
	case "title":
		order = `s.title ASC,s.id`
	case "difficulty":
		order = guitarStarsSQL + ` ASC,s.created_at DESC,s.id`
	default:
		return fail(400, "invalid_sort", "排序无效。")
	}
	var owner any
	if r.URL.Query().Get("favorite") == "1" {
		p, err := a.authenticate(r)
		if err != nil {
			return err
		}
		if expected := r.Header.Get("X-Kim-Expected-User"); expected != "" && expected != p.UserID {
			return fail(409, "account_changed", "账号已切换，请刷新后重试。")
		}
		owner = p.UserID
	}
	var artist any
	if artistID != "" {
		if !validID(artistID) {
			return fail(400, "invalid_artist", "歌手参数无效。")
		}
		var name, avatarURL string
		if err := a.db.QueryRow(r.Context(), `SELECT a.name,`+guitarArtistAvatarURL+` FROM guitar_artists a WHERE a.app_id=$1 AND a.id=$2`, guitarAppID, artistID).Scan(&name, &avatarURL); err != nil {
			return err
		}
		artist = map[string]any{"id": artistID, "name": name, "avatarUrl": avatarURL}
	}
	if utf8.RuneCountInString(query) > 100 {
		return fail(400, "invalid_query", "曲谱名称最多100字。")
	}
	if difficulty != "" && difficulty != "入门" && difficulty != "初级" && difficulty != "中级" {
		return fail(400, "invalid_difficulty", "难度无效。")
	}
	var stars any
	if value := r.URL.Query().Get("difficultyStars"); value != "" {
		value, err := guitarStars(value)
		if err != nil {
			return err
		}
		stars = value
	}
	var repertoireStars any
	if value := r.URL.Query().Get("repertoireStars"); value != "" {
		rating, err := guitarStars(value)
		if err != nil {
			return err
		}
		repertoireStars = rating
	}
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	pageSize, err := guitarCatalogNumber(r, "pageSize", 20, 50)
	if err != nil {
		return err
	}
	// Treat wildcard characters as part of the title, not as query syntax.
	pattern := "%" + strings.NewReplacer(`\`, `\\`, `%`, `\%`, `_`, `\_`).Replace(query) + "%"
	where := ` FROM guitar_scores s WHERE s.app_id=$1 AND s.status='published'
 AND ($2='' OR s.title ILIKE $3 ESCAPE '\' OR ($11 AND s.artist ILIKE $3 ESCAPE '\'))
 AND (cardinality($4::text[])=0 OR s.category=ANY($4)) AND ($5='' OR s.difficulty=$5)
 AND ($6::double precision IS NULL OR ` + guitarStarsSQL + `=$6)
 AND ($7::uuid IS NULL OR s.artist_id=$7::uuid)
 AND (cardinality($8::text[])=0 OR ` + guitarCatalogKey + `=ANY($8))
 AND (cardinality($9::text[])=0 OR (CASE WHEN ` + guitarStarsSQL + `<=2 THEN 'beginner'
 WHEN ` + guitarStarsSQL + `<=3.5 THEN 'intermediate' ELSE 'advanced' END)=ANY($9))
 AND ($10::uuid IS NULL OR EXISTS(SELECT 1 FROM guitar_favorites f WHERE f.app_id=s.app_id AND f.user_id=$10 AND f.kind='score' AND f.target_id=s.id))
 AND ($12::double precision IS NULL OR (s.category IN ('弹唱','指弹') AND ` + guitarStarsSQL + ` >= $12))`
	var filterArtist any
	if artistID != "" {
		filterArtist = artistID
	}
	args := []any{guitarAppID, query, pattern, category, difficulty, stars, filterArtist, key, level, owner, r.URL.Query().Get("match") == "artist", repertoireStars}
	var total int
	if err = a.db.QueryRow(r.Context(), "SELECT count(*)"+where, args...).Scan(&total); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT s.id,s.title,s.artist,s.description,s.category,s.difficulty,s.credit,s.mime,s.created_at,`+guitarStarsSQL+`,`+guitarKeyColumns+`,s.artist_id::text,`+guitarCatalogBPM+where+` ORDER BY `+order+` LIMIT $13 OFFSET $14`, append(args, pageSize, (page-1)*pageSize)...)
	if err != nil {
		return err
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, artist, description, kind, level, credit string
		var mime *string
		var scoreArtistID *string
		var created time.Time
		var rating float64
		var bpm *int
		var card guitarCard
		if err = rows.Scan(&id, &title, &artist, &description, &kind, &level, &credit, &mime, &created, &rating, &card.key, &card.chords, &card.meter, &card.capo, &scoreArtistID, &bpm); err != nil {
			return err
		}
		card.artist = artist
		card.description = description
		card.category = kind
		card.stars = rating
		card.mime = mime
		row := map[string]any{"id": id, "title": title, "difficulty": level, "credit": credit, "createdAt": created, "artistId": scoreArtistID}
		card.apply(row)
		row["bpm"] = bpm
		out = append(out, row)
	}
	if err = rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"scores": out, "artist": artist, "pagination": map[string]any{"page": page, "pageSize": pageSize, "total": total, "totalPages": (total + pageSize - 1) / pageSize}})
	return nil
}
