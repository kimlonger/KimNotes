package main

import (
	"net/http"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const guitarCatalogKey = `COALESCE(s.attributes->>'key',NULLIF(s.notation->>'key',''),substring(s.description from '([A-G][#b]?)调'),'')`
const guitarCatalogBPM = `COALESCE((s.attributes->>'bpm')::int,CASE WHEN s.notation->>'bpm' ~ '^[0-9]{1,3}$' THEN (s.notation->>'bpm')::int END,
 substring(s.description from '([0-9]{2,3})[[:space:]]*[Bb][Pp][Mm]')::int)`

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
	query := strings.TrimSpace(r.URL.Query().Get("q"))
	category, difficulty := r.URL.Query().Get("category"), r.URL.Query().Get("difficulty")
	artistID := r.URL.Query().Get("artistId")
	key, level := r.URL.Query().Get("key"), r.URL.Query().Get("level")
	if key != "" && key != "C" && key != "G" && key != "D" {
		return fail(400, "invalid_key", "调式无效。")
	}
	if level != "" && level != "beginner" && level != "intermediate" && level != "advanced" {
		return fail(400, "invalid_level", "难度无效。")
	}
	order := `s.created_at DESC,s.id`
	switch r.URL.Query().Get("sort") {
	case "":
	case "bpm":
		order = guitarCatalogBPM + ` ASC NULLS LAST,s.created_at DESC,s.id`
	case "title":
		order = `s.title ASC,s.id`
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
	if category != "" && category != "弹唱" && category != "指弹" && category != "练习" {
		return fail(400, "invalid_category", "曲谱分类无效。")
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
	const where = ` FROM guitar_scores s WHERE s.app_id=$1 AND s.status='published'
 AND ($2='' OR s.title ILIKE $3 ESCAPE '\' OR ($11 AND s.artist ILIKE $3 ESCAPE '\'))
 AND ($4='' OR s.category=$4) AND ($5='' OR s.difficulty=$5)
 AND ($6::double precision IS NULL OR ` + guitarStarsSQL + `=$6)
 AND ($7::uuid IS NULL OR s.artist_id=$7::uuid)
 AND ($8='' OR ` + guitarCatalogKey + `=$8)
 AND ($9='' OR CASE $9 WHEN 'beginner' THEN ` + guitarStarsSQL + `<=2
 WHEN 'intermediate' THEN ` + guitarStarsSQL + `>2 AND ` + guitarStarsSQL + `<=3.5 ELSE ` + guitarStarsSQL + `>3.5 END)
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
	rows, err := a.db.Query(r.Context(), `SELECT s.id,s.title,s.artist,s.genre,s.description,s.category,s.difficulty,s.credit,s.mime,s.created_at,`+guitarStarsSQL+`,`+guitarKeyColumns+`,s.artist_id::text,`+guitarCatalogBPM+where+` ORDER BY `+order+` LIMIT $13 OFFSET $14`, append(args, pageSize, (page-1)*pageSize)...)
	if err != nil {
		return err
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, artist, genre, description, kind, level, credit string
		var mime *string
		var scoreArtistID *string
		var created time.Time
		var rating float64
		var bpm *int
		var card guitarCard
		if err = rows.Scan(&id, &title, &artist, &genre, &description, &kind, &level, &credit, &mime, &created, &rating, &card.key, &card.chords, &card.meter, &card.capo, &scoreArtistID, &bpm); err != nil {
			return err
		}
		card.artist = artist
		card.description = description
		card.category = kind
		card.stars = rating
		card.mime = mime
		row := map[string]any{"id": id, "title": title, "genre": genre, "difficulty": level, "credit": credit, "createdAt": created, "artistId": scoreArtistID}
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

func guitarScoreMetadata(artist, genre string) (string, string, error) {
	name, err := guitarText(artist, 0, 120)
	if err != nil {
		return "", "", err
	}
	genre = strings.TrimSpace(genre)
	if genre != "" && genre != "民谣" && genre != "摇滚" && genre != "其他" {
		return "", "", fail(400, "invalid_genre", "曲谱风格无效。")
	}
	return name, genre, nil
}
