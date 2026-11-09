package main

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"
)

func guitarHomeScore(t *testing.T, e *testEnv, id, artist, category, status string, age int) {
	t.Helper()
	_, err := e.db.Exec(context.Background(), `INSERT INTO guitar_scores(id,title,artist,category,difficulty,difficulty_stars,notation,credit,status,created_at)
 VALUES($1,$1,$2,$3,'初级',2.5,'{}','test',$4,now()-$5*interval '1 hour')`, id, artist, category, status, age)
	if err != nil {
		t.Fatal(err)
	}
}

func TestGuitarHomeAutomaticRecommendationsAndArtistFilter(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	var users []string
	var cookies []*http.Cookie
	for i := 0; i < 3; i++ {
		cookie := guitarTestAccount(t, e, fmt.Sprintf("home-fan-%d", i))
		cookies = append(cookies, cookie)
		_, out, _ := e.request("GET", "guitar/session", nil, cookie)
		users = append(users, out["user"].(map[string]any)["id"].(string))
	}
	for i, score := range []struct{ id, artist, category, status string }{
		{"home-a1", "赵雷", "弹唱", "published"},
		{"home-a2", " 赵 雷 ", "指弹", "published"},
		{"home-a3", "赵雷", "弹唱", "published"},
		{"home-b", "歌手乙", "弹唱", "published"},
		{"home-old", "歌手丙", "弹唱", "published"},
		{"home-archived", "归档歌手", "弹唱", "archived"},
		{"home-practice", "练习作者", "练习", "published"},
		{"home-c", "歌手丁", "指弹", "published"},
		{"home-d", "歌手戊", "弹唱", "published"},
		{"home-unknown1", "", "弹唱", "published"},
		{"home-unknown2", "", "弹唱", "published"},
		{"home-unknown3", "", "指弹", "published"},
	} {
		guitarHomeScore(t, e, score.id, score.artist, score.category, score.status, 100-i)
	}
	for _, pair := range []struct {
		score      string
		count, age int
	}{
		{"home-a1", 3, 0}, {"home-a2", 2, 0}, {"home-a3", 1, 0},
		{"home-b", 1, 0}, {"home-old", 3, 31}, {"home-archived", 3, 0}, {"home-practice", 3, 0},
	} {
		for i := 0; i < pair.count; i++ {
			if _, err := e.db.Exec(ctx, `INSERT INTO guitar_favorites(user_id,kind,target_id,created_at)
 VALUES($1,'score',$2,now()-$3*interval '1 day')`, users[i], pair.score, pair.age); err != nil {
				t.Fatal(err)
			}
		}
	}
	// A favorite of another kind must never boost a score with the same target ID.
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_favorites(user_id,kind,target_id) VALUES($1,'course','home-old')`, users[0]); err != nil {
		t.Fatal(err)
	}
	s, out, _ := e.request("GET", "guitar/home", nil, nil)
	if s != 200 {
		t.Fatal(s, out)
	}
	scores := out["scores"].([]any)
	if len(scores) != 9 || scores[0].(map[string]any)["id"] != "home-a1" || scores[1].(map[string]any)["id"] != "home-a2" || scores[2].(map[string]any)["id"] != "home-b" {
		t.Fatal("automatic ranking or backfill", out)
	}
	for _, value := range scores {
		row := value.(map[string]any)
		if row["id"] == "home-a3" || row["id"] == "home-archived" || row["id"] == "home-practice" {
			t.Fatal("artist cap or publication/type isolation", row)
		}
		for _, key := range []string{"notation", "bytes", "email"} {
			if _, ok := row[key]; ok {
				t.Fatal("unnecessary public data", key)
			}
		}
	}
	artistID := scores[0].(map[string]any)["artistId"].(string)
	if artistID != scores[1].(map[string]any)["artistId"] {
		t.Fatal("artist names were not resolved")
	}
	artists := out["artists"].([]any)
	first := artists[0].(map[string]any)
	if first["id"] != artistID || first["scoreCount"] != float64(3) || first["recentFavoriteCount"] != float64(6) {
		t.Fatal("artist aggregation", artists)
	}
	for _, value := range artists {
		name := value.(map[string]any)["name"]
		if name == "归档歌手" || name == "练习作者" {
			t.Fatal("non-song/archived artist exposed", name)
		}
	}
	_, one, _ := e.request("GET", "guitar/scores?artistId="+artistID+"&pageSize=1", nil, nil)
	_, two, _ := e.request("GET", "guitar/scores?artistId="+artistID+"&pageSize=1&page=2", nil, nil)
	if one["pagination"].(map[string]any)["total"] != float64(3) || one["scores"].([]any)[0].(map[string]any)["id"] == two["scores"].([]any)[0].(map[string]any)["id"] {
		t.Fatal("artist filtering must paginate", one, two)
	}
	_, search, _ := e.request("GET", "guitar/scores?artistId="+artistID+"&q="+url.QueryEscape("赵雷"), nil, nil)
	if len(search["scores"].([]any)) != 0 {
		t.Fatal("search must remain title-only", search)
	}
	if s, _, _ := e.request("GET", "guitar/scores?artistId=invalid", nil, nil); s != 400 {
		t.Fatal("invalid artist", s)
	}
	// Unfavoriting changes the next public query; there is no stale counter to maintain.
	if s, _, _ := e.request("DELETE", "guitar/favorites/score/home-a1", nil, cookies[0]); s != 200 {
		t.Fatal(s)
	}
	_, changed, _ := e.request("GET", "guitar/home", nil, nil)
	for _, value := range changed["scores"].([]any) {
		row := value.(map[string]any)
		if row["id"] == "home-a1" && (row["favoriteCount"] != float64(2) || row["recentFavoriteCount"] != float64(2)) {
			t.Fatal("favorite removal stale", row)
		}
	}
	if s, _, _ := e.request("GET", "main/home", nil, nil); s != 404 {
		t.Fatal("app isolation", s)
	}
}

func TestGuitarLearningStarsPeriodsAndPublicFields(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	var today, week, month time.Time
	if err := e.db.QueryRow(ctx, `SELECT (now() AT TIME ZONE 'Asia/Shanghai')::date,date_trunc('week',now() AT TIME ZONE 'Asia/Shanghai')::date,date_trunc('month',now() AT TIME ZONE 'Asia/Shanghai')::date`).Scan(&today, &week, &month); err != nil {
		t.Fatal(err)
	}
	expected := map[string]map[string]int{"week": {}, "month": {}}
	for i := 0; i < 4; i++ {
		cookie := guitarTestAccount(t, e, fmt.Sprintf("home-star-%d", i))
		_, session, _ := e.request("GET", "guitar/session", nil, cookie)
		id := session["user"].(map[string]any)["id"].(string)
		name := fmt.Sprintf("琴友%d", i)
		if _, err := e.db.Exec(ctx, `UPDATE guitar_members SET nickname=$2,avatar='leaf' WHERE user_id=$1`, id, name); err != nil {
			t.Fatal(err)
		}
		dates := []time.Time{today, week.AddDate(0, 0, -1), month.AddDate(0, 0, -1), today.AddDate(0, 0, 1)}
		for j := 1; j <= i; j++ {
			dates = append(dates, today.AddDate(0, 0, -j))
		}
		unique := map[string]bool{}
		for _, date := range dates {
			key := date.Format("2006-01-02")
			if unique[key] {
				continue
			}
			unique[key] = true
			if _, err := e.db.Exec(ctx, `INSERT INTO guitar_visits(user_id,visit_date) VALUES($1,$2::date) ON CONFLICT DO NOTHING`, id, key); err != nil {
				t.Fatal(err)
			}
			for period, start := range map[string]time.Time{"week": week, "month": month} {
				if !date.Before(start) && !date.After(today) {
					expected[period][name]++
				}
			}
		}
		for j := 0; j < 2; j++ {
			if status, _, _ := e.request("POST", "guitar/visits", nil, cookie); status != 200 {
				t.Fatal(status)
			}
		}
	}
	for _, period := range []string{"week", "month"} {
		status, out, _ := e.request("GET", "guitar/learning-stars?period="+period, nil, nil)
		if status != 200 {
			t.Fatal(status, out)
		}
		start := week
		if period == "month" {
			start = month
		}
		if out["periodStart"] != start.Format("2006-01-02") {
			t.Fatal("wrong calendar start", out)
		}
		stars := out["stars"].([]any)
		if len(stars) != 3 {
			t.Fatal("not top three", out)
		}
		last := 100
		for _, value := range stars {
			row := value.(map[string]any)
			name := row["nickname"].(string)
			days := int(row["days"].(float64))
			if days != expected[period][name] || days > last {
				t.Fatal("wrong period/dedup/future exclusion", out, expected)
			}
			last = days
			for key := range row {
				if key != "nickname" && key != "avatar" && key != "avatarUrl" && key != "days" {
					t.Fatal("private profile field exposed", key)
				}
			}
		}
		if strings.Contains(fmt.Sprint(out), "@example.com") {
			t.Fatal("email leaked", out)
		}
	}
	if status, _, _ := e.request("GET", "guitar/learning-stars?period=all", nil, nil); status != 400 {
		t.Fatal("invalid period", status)
	}
}

func TestGuitarArtistMigrationBackfillAndReassignment(t *testing.T) {
	e := newTestEnv(t)
	ctx := context.Background()
	for _, schema := range []string{guitarSchema, guitarCatalogSchema, guitarOriginSchema, guitarExperienceSchema, guitarPagesSchema, guitarStarsSchema} {
		if _, err := e.db.Exec(ctx, schema); err != nil {
			t.Fatal(err)
		}
	}
	guitarHomeScore(t, e, "backfill-one", " 赵雷 ", "弹唱", "published", 1)
	guitarHomeScore(t, e, "backfill-two", "赵　雷", "指弹", "published", 2)
	if _, err := e.db.Exec(ctx, guitarHomeSchema); err != nil {
		t.Fatal(err)
	}
	var count, ids int
	if err := e.db.QueryRow(ctx, `SELECT count(*),count(DISTINCT artist_id) FROM guitar_scores WHERE artist_id IS NOT NULL`).Scan(&count, &ids); err != nil || count != 2 || ids != 1 {
		t.Fatal("backfill lost relationship", count, ids, err)
	}
	if _, err := e.db.Exec(ctx, `UPDATE guitar_scores SET artist='新歌手' WHERE id='backfill-one'`); err != nil {
		t.Fatal(err)
	}
	if err := e.db.QueryRow(ctx, `SELECT count(DISTINCT artist_id) FROM guitar_scores`).Scan(&ids); err != nil || ids != 2 {
		t.Fatal("artist reassignment", ids, err)
	}
	if _, err := e.db.Exec(ctx, `UPDATE guitar_scores SET artist='' WHERE id='backfill-one'`); err != nil {
		t.Fatal(err)
	}
	var cleared bool
	if err := e.db.QueryRow(ctx, `SELECT artist_id IS NULL FROM guitar_scores WHERE id='backfill-one'`).Scan(&cleared); err != nil || !cleared {
		t.Fatal("unknown artist not cleared", err)
	}
}
