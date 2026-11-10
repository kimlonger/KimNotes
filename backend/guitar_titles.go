package main

import (
	"context"
	"strings"

	"golang.org/x/text/cases"
	"golang.org/x/text/language"
	"golang.org/x/text/unicode/norm"
)

// The IDs are stable even if the display names in workbench-model.js change.
var guitarSongTitleGoals = [...]struct{ songs, twoStars, threeStars int }{
	{0, 0, 0}, {1, 0, 0}, {3, 1, 0}, {6, 3, 0}, {10, 6, 0}, {15, 10, 1}, {25, 18, 1},
}

var guitarTimeTitleMinutes = [...]int64{15, 30, 60, 600, 3000, 6000, 60000}

func (a *App) guitarEquipTitle(ctx context.Context, user, id string) error {
	if id != "" {
		kind, level, ok := strings.Cut(id, "-")
		if !ok || len(level) != 1 || level[0] < '1' || level[0] > '7' {
			return fail(400, "invalid_title", "没有这个称号。")
		}
		index := int(level[0] - '1')
		var unlocked bool
		switch kind {
		case "songs":
			songs, twoStars, threeStars, err := a.guitarLearnedTitleCounts(ctx, user)
			if err != nil {
				return err
			}
			goal := guitarSongTitleGoals[index]
			unlocked = songs >= goal.songs && twoStars >= goal.twoStars && threeStars >= goal.threeStars
		case "time":
			var seconds int64
			if err := a.db.QueryRow(ctx, `SELECT COALESCE(sum(seconds),0) FROM (`+guitarPracticeEvents+`) e WHERE user_id=$1`, user).Scan(&seconds); err != nil {
				return err
			}
			unlocked = seconds/60 >= guitarTimeTitleMinutes[index]
		default:
			return fail(400, "invalid_title", "没有这个称号。")
		}
		if !unlocked {
			return fail(403, "title_locked", "这个称号还没解锁。")
		}
	}
	_, err := a.db.Exec(ctx, `INSERT INTO guitar_practice_preferences(user_id,equipped_title)
 VALUES($1,$2) ON CONFLICT(app_id,user_id) DO UPDATE SET equipped_title=EXCLUDED.equipped_title`, user, id)
	return err
}

func (a *App) guitarLearnedTitleCounts(ctx context.Context, user string) (songs, twoStars, threeStars int, err error) {
	rows, err := a.db.Query(ctx, `SELECT s.title,s.artist,`+guitarStarsSQL+`
 FROM guitar_reading_progress r JOIN guitar_scores s ON s.app_id=r.app_id AND s.id=r.score_id
 WHERE r.user_id=$1 AND r.learned AND s.status='published' AND s.category IN ('弹唱','指弹')`, user)
	if err != nil {
		return 0, 0, 0, err
	}
	defer rows.Close()
	type songKey struct{ title, artist string }
	ratings := make(map[songKey]float64)
	lower := cases.Lower(language.Und)
	normalize := func(value string) string {
		return lower.String(strings.Join(strings.Fields(norm.NFKC.String(value)), " "))
	}
	for rows.Next() {
		var title, artist string
		var rating float64
		if err = rows.Scan(&title, &artist, &rating); err != nil {
			return 0, 0, 0, err
		}
		key := songKey{normalize(title), normalize(artist)}
		ratings[key] = max(ratings[key], rating)
	}
	if err = rows.Err(); err != nil {
		return 0, 0, 0, err
	}
	for _, rating := range ratings {
		if rating >= 2 {
			twoStars++
		}
		if rating >= 3 {
			threeStars++
		}
	}
	return len(ratings), twoStars, threeStars, nil
}
