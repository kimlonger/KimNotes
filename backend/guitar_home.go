package main

import (
	"encoding/json"
	"net/http"
	"time"
)

// A favorite is counted only while it exists. Its primary key already deduplicates
// each member/score pair; no administrator weights or derived scores are stored.
const guitarHomeFavorites = `WITH favorite_counts AS (
 SELECT target_id,count(*) AS total,
 count(*) FILTER(WHERE created_at>=now()-interval '30 days') AS recent
 FROM guitar_favorites WHERE app_id=$1 AND kind='score' AND created_at<=now()
 GROUP BY target_id
) `

const guitarArtistSelect = `SELECT a.id::text,a.name,` + guitarArtistAvatarURL + `,count(*),
 COALESCE(sum(f.recent),0)::bigint,
 (SELECT COALESCE(jsonb_agg(jsonb_build_object('id',p.id,'title',p.title,'key',p.key) ORDER BY p.created_at DESC,p.id),'[]'::jsonb)
  FROM (SELECT p.id,p.title,p.created_at,COALESCE(p.attributes->>'key',NULLIF(p.notation->>'key',''),substring(p.description from '([A-G][#b]?)调'),'') AS key FROM guitar_scores p
        WHERE p.app_id=a.app_id AND p.artist_id=a.id AND p.status='published' AND p.category IN ('弹唱','指弹')
        ORDER BY p.created_at DESC,p.id LIMIT 3) p)
 FROM guitar_artists a
 JOIN guitar_scores s ON s.app_id=a.app_id AND s.artist_id=a.id
 LEFT JOIN favorite_counts f ON f.target_id=s.id
 WHERE a.app_id=$1 AND s.status='published' AND s.category IN ('弹唱','指弹')
 GROUP BY a.app_id,a.id,a.name
 ORDER BY COALESCE(sum(f.recent),0) DESC,count(*) DESC,max(s.created_at) DESC,a.name,a.id LIMIT $2 OFFSET $3`

func (a *App) guitarArtistList(r *http.Request, limit, offset int) ([]map[string]any, error) {
	rows, err := a.db.Query(r.Context(), guitarHomeFavorites+guitarArtistSelect, guitarAppID, limit, offset)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	artists := []map[string]any{}
	for rows.Next() {
		var id, name, avatarURL string
		var count, recent int
		var songs json.RawMessage
		if err = rows.Scan(&id, &name, &avatarURL, &count, &recent, &songs); err != nil {
			return nil, err
		}
		artists = append(artists, map[string]any{"id": id, "name": name, "avatarUrl": avatarURL, "scoreCount": count, "recentFavoriteCount": recent, "scores": songs})
	}
	return artists, rows.Err()
}

func (a *App) guitarArtists(w http.ResponseWriter, r *http.Request) error {
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	pageSize, err := guitarCatalogNumber(r, "pageSize", 20, 50)
	if err != nil {
		return err
	}
	var total int
	if err = a.db.QueryRow(r.Context(), `SELECT count(DISTINCT artist_id) FROM guitar_scores
 WHERE app_id=$1 AND status='published' AND category IN ('弹唱','指弹')`, guitarAppID).Scan(&total); err != nil {
		return err
	}
	artists, err := a.guitarArtistList(r, pageSize, (page-1)*pageSize)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"artists": artists, "pagination": guitarHomePagination(page, pageSize, total)})
	return nil
}

func guitarHomePagination(page, pageSize, total int) map[string]int {
	return map[string]int{"page": page, "pageSize": pageSize, "total": total, "totalPages": (total + pageSize - 1) / pageSize}
}

func (a *App) guitarHome(w http.ResponseWriter, r *http.Request) error {
	rows, err := a.db.Query(r.Context(), guitarHomeFavorites+`, candidates AS (
 SELECT s.id,s.artist_id,s.created_at,COALESCE(f.total,0) AS favorites,
 COALESCE(f.recent,0) AS recent,
 row_number() OVER(PARTITION BY COALESCE(s.artist_id::text,'score:'||s.id)
 ORDER BY COALESCE(f.recent,0) DESC,s.created_at DESC,s.id) AS artist_position
 FROM guitar_scores s LEFT JOIN favorite_counts f ON f.target_id=s.id
 WHERE s.app_id=$1 AND s.status='published' AND s.category IN ('弹唱','指弹')
 ) SELECT s.id,s.title,`+guitarCardColumns+`,s.artist_id::text,`+guitarArtistAvatarURL+`,c.favorites,c.recent
 FROM candidates c JOIN guitar_scores s ON s.app_id=$1 AND s.id=c.id
 LEFT JOIN guitar_artists a ON a.app_id=s.app_id AND a.id=s.artist_id
 WHERE c.artist_position<=2 ORDER BY c.recent DESC,c.created_at DESC,c.id LIMIT 16`, guitarAppID)
	if err != nil {
		return err
	}
	scores := []map[string]any{}
	for rows.Next() {
		var id, title, artistAvatarURL string
		var artistID *string
		var favorites, recent int
		var card guitarCard
		fields := append([]any{&id, &title}, card.scanFields()...)
		if err = rows.Scan(append(fields, &artistID, &artistAvatarURL, &favorites, &recent)...); err != nil {
			rows.Close()
			return err
		}
		row := map[string]any{"id": id, "title": title, "artistId": artistID, "artistAvatarUrl": artistAvatarURL, "favoriteCount": favorites, "recentFavoriteCount": recent}
		card.apply(row)
		scores = append(scores, row)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return err
	}
	artists, err := a.guitarArtistList(r, 12, 0)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"scores": scores, "artists": artists})
	return nil
}

// The public board measures daily opens, not an inferred skill or practice time.
func (a *App) guitarLearningStars(w http.ResponseWriter, r *http.Request) error {
	period := r.URL.Query().Get("period")
	if period == "" {
		period = "week"
	}
	if period != "week" && period != "month" {
		return fail(400, "invalid_period", "请选择本周或本月。")
	}
	page, err := guitarCatalogNumber(r, "page", 1, 1000000)
	if err != nil {
		return err
	}
	pageSize, err := guitarCatalogNumber(r, "pageSize", 3, 50)
	if err != nil {
		return err
	}
	var today, start, end time.Time
	if err := a.db.QueryRow(r.Context(), `SELECT (now() AT TIME ZONE 'Asia/Shanghai')::date,
 date_trunc($1,now() AT TIME ZONE 'Asia/Shanghai')::date,
 (date_trunc($1,now() AT TIME ZONE 'Asia/Shanghai')+CASE WHEN $1='week' THEN interval '7 days' ELSE interval '1 month' END)::date`, period).Scan(&today, &start, &end); err != nil {
		return err
	}
	if r.URL.Query().Get("measure") == "practice" {
		return a.guitarPracticeStars(w, r, period, start, today, end, page, pageSize)
	}
	var total int
	if err = a.db.QueryRow(r.Context(), `SELECT count(DISTINCT user_id) FROM guitar_visits
 WHERE app_id=$1 AND visit_date>=$2::date AND visit_date<$3::date AND visit_date<=$4::date`, guitarAppID, start, end, today).Scan(&total); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `SELECT COALESCE(m.nickname,'琴友'),COALESCE(m.avatar,'guitar'),
 CASE WHEN m.avatar_bytes IS NOT NULL THEN '/api/v1/apps/guitar/avatars/'||v.user_id::text||'?v='||extract(epoch FROM m.updated_at)::text ELSE '' END,
 count(*) AS days FROM guitar_visits v
 LEFT JOIN guitar_members m ON m.app_id=v.app_id AND m.user_id=v.user_id
 WHERE v.app_id=$1 AND v.visit_date>=$2::date AND v.visit_date<$3::date AND v.visit_date<=$4::date
 GROUP BY v.user_id,m.nickname,m.avatar,m.avatar_bytes IS NOT NULL,m.updated_at
 ORDER BY days DESC,max(v.visit_date) DESC,v.user_id LIMIT $5 OFFSET $6`, guitarAppID, start, end, today, pageSize, (page-1)*pageSize)
	if err != nil {
		return err
	}
	defer rows.Close()
	stars := []map[string]any{}
	for rows.Next() {
		var nickname, avatar, avatarURL string
		var days int
		if err = rows.Scan(&nickname, &avatar, &avatarURL, &days); err != nil {
			return err
		}
		stars = append(stars, map[string]any{"nickname": nickname, "avatar": avatar, "avatarUrl": avatarURL, "days": days})
	}
	if err = rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"period": period, "periodStart": start.Format("2006-01-02"), "periodEnd": end.AddDate(0, 0, -1).Format("2006-01-02"), "stars": stars, "pagination": guitarHomePagination(page, pageSize, total)})
	return nil
}

func (a *App) guitarPracticeStars(w http.ResponseWriter, r *http.Request, period string, start, today, end time.Time, page, size int) error {
	var data json.RawMessage
	err := a.db.QueryRow(r.Context(), `
 WITH totals AS (
  SELECT user_id,sum(seconds) AS seconds FROM (`+guitarPracticeEvents+`) e
  WHERE day BETWEEN $1::date AND $2::date GROUP BY user_id HAVING sum(seconds)>=60
 ), ranks AS (
  SELECT COALESCE(m.nickname,'琴友') AS nickname,COALESCE(m.avatar,'guitar') AS avatar,
   CASE WHEN m.avatar_bytes IS NOT NULL THEN '/api/v1/apps/guitar/avatars/'||t.user_id::text||'?v='||extract(epoch FROM m.updated_at)::text ELSE '' END AS "avatarUrl",
   t.seconds,t.user_id
  FROM totals t LEFT JOIN guitar_members m ON m.app_id='guitar' AND m.user_id=t.user_id
  ORDER BY t.seconds DESC,t.user_id LIMIT $3 OFFSET $4
 )
 SELECT jsonb_build_object('stars',COALESCE((SELECT jsonb_agg(to_jsonb(r)-'user_id' ORDER BY seconds DESC,user_id) FROM ranks r),'[]'),
 'total',(SELECT count(*) FROM totals))`, start, today, size, (page-1)*size).Scan(&data)
	if err != nil {
		return err
	}
	var result struct {
		Stars json.RawMessage `json:"stars"`
		Total int             `json:"total"`
	}
	if err := json.Unmarshal(data, &result); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"period": period, "periodStart": start.Format("2006-01-02"), "periodEnd": end.AddDate(0, 0, -1).Format("2006-01-02"),
		"stars": result.Stars, "pagination": guitarHomePagination(page, size, result.Total)})
	return nil
}
