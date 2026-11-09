package main

import (
	"context"
	"testing"
)

func TestGuitarProfileCardsAndPracticeCounts(t *testing.T) {
	e := guitarTestEnv(t)
	owner, other := guitarTestAccount(t, e, "card-owner"), guitarTestAccount(t, e, "card-other")
	_, session, _ := e.request("GET", "guitar/session", nil, owner)
	uid := session["user"].(map[string]any)["id"].(string)
	_, session, _ = e.request("GET", "guitar/session", nil, other)
	otherID := session["user"].(map[string]any)["id"].(string)
	ctx := context.Background()
	if _, err := e.db.Exec(ctx, `UPDATE guitar_scores SET artist='测试歌手',difficulty_stars=2.5,notation=jsonb_set(jsonb_set(notation,'{key}','"G"'),'{chords}','["G","D","Em"]') WHERE app_id='guitar' AND id='course-little-bee'`); err != nil {
		t.Fatal(err)
	}
	// Two completed sessions on the same score count as one practiced score.
	for _, row := range []struct {
		id, user, score string
		seconds         int
		ended           bool
	}{
		{"card-a", uid, "course-little-bee", 90, true}, {"card-b", uid, "course-little-bee", 30, true},
		{"card-c", uid, "course-seven-notes", 60, true}, {"card-zero", uid, "course-seven-notes", 0, true},
		{"card-running", uid, "course-seven-notes", 0, false}, {"card-other", otherID, "course-little-bee", 999, true},
	} {
		if _, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_sessions(id,user_id,score_id,started_at,ended_at,seconds) VALUES($1,$2,$3,now()-interval '3 minutes',CASE WHEN $5 THEN now() END,$4)`, newID(), row.user, row.score, row.seconds, row.ended); err != nil {
			t.Fatal(err)
		}
	}
	status, out, _ := e.request("GET", "guitar/practice?pageSize=1", nil, owner)
	if status != 200 || out["sessions"] != float64(3) || out["practicedScores"] != float64(2) || out["totalSeconds"] != float64(180) {
		t.Fatal(status, out)
	}
	if out["recentPagination"].(map[string]any)["totalPages"] != float64(2) {
		t.Fatal(out)
	}
	status, second, _ := e.request("GET", "guitar/practice?page=2&pageSize=1", nil, owner)
	if status != 200 || len(second["recent"].([]any)) != 1 || second["recent"].([]any)[0].(map[string]any)["id"] == out["recent"].([]any)[0].(map[string]any)["id"] {
		t.Fatal(status, out, second)
	}
	if s, _, _ := e.request("GET", "guitar/practice?pageSize=25", nil, owner); s != 400 {
		t.Fatal("unbounded history", s)
	}
	e.request("PUT", "guitar/favorites/score/course-little-bee", nil, owner)
	status, out, _ = e.request("GET", "guitar/favorites", nil, owner)
	if status != 200 {
		t.Fatal(status, out)
	}
	card := out["favorites"].([]any)[0].(map[string]any)
	if card["artist"] != "测试歌手" || card["difficultyStars"] != 2.5 || card["key"] != "G" || card["lastPracticedAt"] == nil || len(card["chords"].([]any)) != 3 {
		t.Fatal(card)
	}
	if _, ok := card["notation"]; ok {
		t.Fatal("full notation leaked into cards")
	}
	_, out, _ = e.request("GET", "guitar/favorites", nil, other)
	if len(out["favorites"].([]any)) != 0 {
		t.Fatal("favorites crossed accounts", out)
	}
	// Deleting/restoring membership is idempotent, as required by the Undo action.
	e.request("DELETE", "guitar/favorites/score/course-little-bee", nil, owner)
	e.request("PUT", "guitar/favorites/score/course-little-bee", nil, owner)
	e.request("PUT", "guitar/favorites/score/course-little-bee", nil, owner)
	_, out, _ = e.request("GET", "guitar/favorites", nil, owner)
	if len(out["favorites"].([]any)) != 1 {
		t.Fatal("undo duplicated favorites", out)
	}
	if _, err := e.db.Exec(ctx, `UPDATE guitar_scores SET status='archived' WHERE app_id='guitar' AND id='course-little-bee'`); err != nil {
		t.Fatal(err)
	}
	status, out, _ = e.request("GET", "guitar/practice", nil, owner)
	if status != 200 || out["sessions"] != float64(3) {
		t.Fatal(status, out)
	}
	for _, item := range out["recent"].([]any) {
		c := item.(map[string]any)
		if c["id"] == "course-little-bee" && (c["available"] != false || c["title"] != "曲谱已下架") {
			t.Fatal("archived score stayed actionable", c)
		}
	}
}
