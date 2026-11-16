package main

import (
	"net/http"
	"time"
)

// guitarPopularScores counts people, not sessions or favorite clicks. The week
// follows the same Asia/Shanghai Monday boundary as the practice leaderboard.
func (a *App) guitarPopularScores(w http.ResponseWriter, r *http.Request) error {
	limit, err := guitarCatalogNumber(r, "limit", 20, 50)
	if err != nil {
		return err
	}
	var start, end time.Time
	if err = a.db.QueryRow(r.Context(), `SELECT date_trunc('week',now() AT TIME ZONE 'Asia/Shanghai')::date,
        (date_trunc('week',now() AT TIME ZONE 'Asia/Shanghai') + interval '7 days')::date`).Scan(&start, &end); err != nil {
		return err
	}
	rows, err := a.db.Query(r.Context(), `WITH counts AS (
        SELECT p.score_id, count(DISTINCT p.user_id)::int AS users
        FROM guitar_practice_sessions p
        WHERE p.app_id=$1 AND p.ended_at IS NOT NULL AND p.ended_at<=now() AND p.seconds>0
          AND p.started_at>=($2::date::timestamp AT TIME ZONE 'Asia/Shanghai')
          AND p.started_at<($3::date::timestamp AT TIME ZONE 'Asia/Shanghai') AND p.started_at<=now()
        GROUP BY p.score_id
    ) SELECT s.id,s.title,`+guitarCardColumns+`,s.artist_id::text,`+guitarArtistAvatarURL+`,c.users
      FROM counts c JOIN guitar_scores s ON s.app_id=$1 AND s.id=c.score_id AND s.status='published'
      LEFT JOIN guitar_artists a ON a.app_id=s.app_id AND a.id=s.artist_id
      ORDER BY c.users DESC,s.id LIMIT $4`, guitarAppID, start, end, limit)
	if err != nil {
		return err
	}
	defer rows.Close()
	scores := []map[string]any{}
	for rows.Next() {
		var id, title, artistAvatarURL string
		var artistID *string
		var count int
		var card guitarCard
		fields := append([]any{&id, &title}, card.scanFields()...)
		if err = rows.Scan(append(fields, &artistID, &artistAvatarURL, &count)...); err != nil {
			return err
		}
		row := map[string]any{"id": id, "title": title, "artistId": artistID, "artistAvatarUrl": artistAvatarURL, "practiceUserCount": count}
		card.apply(row)
		scores = append(scores, row)
	}
	if err = rows.Err(); err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"period": "week", "periodStart": start.Format("2006-01-02"), "periodEnd": end.AddDate(0, 0, -1).Format("2006-01-02"), "scores": scores})
	return nil
}
