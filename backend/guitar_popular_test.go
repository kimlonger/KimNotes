package main

import (
	"context"
	"fmt"
	"testing"
	"time"
)

func TestGuitarPopularScoresCountsDistinctWeeklyPractitioners(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	var users []string
	for i := 0; i < 3; i++ {
		cookie := guitarTestAccount(t, e, fmt.Sprintf("popular-%d", i))
		_, result, _ := e.request("GET", "guitar/session", nil, cookie)
		users = append(users, result["user"].(map[string]any)["id"].(string))
	}
	for _, score := range []struct{ id, status string }{
		{"popular-a", "published"}, {"popular-b", "published"}, {"popular-c", "published"},
		{"popular-archived", "archived"}, {"popular-empty", "published"},
	} {
		guitarHomeScore(t, e, score.id, "测试歌手", "弹唱", score.status, 1)
	}
	var monday time.Time
	if err := e.db.QueryRow(ctx, `SELECT date_trunc('week', now() AT TIME ZONE 'Asia/Shanghai') AT TIME ZONE 'Asia/Shanghai'`).Scan(&monday); err != nil {
		t.Fatal(err)
	}
	add := func(user, score string, start time.Time, ended bool, seconds int) {
		t.Helper()
		var end any
		if ended {
			end = monday
		}
		_, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_sessions(id,user_id,score_id,started_at,ended_at,seconds,automatic) VALUES($1,$2,$3,$4,$5,$6,true)`, newID(), user, score, start, end, seconds)
		if err != nil {
			t.Fatal(err)
		}
	}
	// The first instant on Monday is included; duplicate sessions from one user count once.
	add(users[0], "popular-a", monday, true, 60)
	add(users[0], "popular-a", monday, true, 90)
	add(users[1], "popular-a", monday, true, 60)
	add(users[0], "popular-b", monday, true, 60)
	add(users[0], "popular-c", monday, true, 60)
	add(users[2], "popular-b", monday.Add(-time.Second), true, 60)
	add(users[2], "popular-b", time.Now().Add(48*time.Hour), true, 60)
	add(users[2], "popular-b", monday, false, 120)
	add(users[2], "popular-b", monday, true, 0)
	add(users[0], "popular-archived", monday, true, 60)
	status, result, _ := e.request("GET", "guitar/popular-scores", nil, nil)
	if status != 200 {
		t.Fatal(status, result)
	}
	scores := result["scores"].([]any)
	if len(scores) != 3 {
		t.Fatalf("unexpected eligible scores: %#v", result)
	}
	for i, expected := range []struct {
		id    string
		count float64
	}{{"popular-a", 2}, {"popular-b", 1}, {"popular-c", 1}} {
		row := scores[i].(map[string]any)
		if row["id"] != expected.id || row["practiceUserCount"] != expected.count {
			t.Fatalf("rank %d: %#v", i, row)
		}
		for _, field := range []string{"title", "artist", "category", "difficultyStars", "key", "chords", "meter", "capo", "mime", "artistId", "artistAvatarUrl"} {
			if _, ok := row[field]; !ok {
				t.Fatalf("missing card field %q in %#v", field, row)
			}
		}
		for _, field := range []string{"user_id", "userId", "email", "notation"} {
			if _, ok := row[field]; ok {
				t.Fatalf("private field %q exposed", field)
			}
		}
	}
	if result["period"] != "week" || result["periodStart"] != monday.In(time.FixedZone("CST", 8*60*60)).Format("2006-01-02") || result["periodEnd"] != monday.In(time.FixedZone("CST", 8*60*60)).AddDate(0, 0, 6).Format("2006-01-02") {
		t.Fatalf("incorrect week: %#v", result)
	}
	status, limited, _ := e.request("GET", "guitar/popular-scores?limit=1", nil, nil)
	if status != 200 || len(limited["scores"].([]any)) != 1 {
		t.Fatal(status, limited)
	}
	for _, query := range []string{"limit=0", "limit=51", "limit=xyz"} {
		if status, _, _ = e.request("GET", "guitar/popular-scores?"+query, nil, nil); status != 400 {
			t.Fatal(query, status)
		}
	}
	if status, _, _ = e.request("GET", "main/popular-scores", nil, nil); status != 404 {
		t.Fatal("app isolation", status)
	}
	if _, err := e.db.Exec(ctx, `DELETE FROM guitar_practice_sessions`); err != nil {
		t.Fatal(err)
	}
	status, empty, _ := e.request("GET", "guitar/popular-scores", nil, nil)
	if status != 200 || len(empty["scores"].([]any)) != 0 {
		t.Fatal("no artificial backfill", status, empty)
	}
}
