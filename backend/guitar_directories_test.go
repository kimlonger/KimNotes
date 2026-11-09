package main

import (
	"context"
	"fmt"
	"testing"
)

func TestGuitarAllArtistsPaginationAndPublicSongLinks(t *testing.T) {
	e := guitarTestEnv(t)
	for i := 1; i <= 27; i++ {
		status := "published"
		if i == 27 {
			status = "archived"
		}
		guitarHomeScore(t, e, fmt.Sprintf("directory-song-%02d", i), fmt.Sprintf("歌手%02d", i), "弹唱", status, i)
	}
	guitarHomeScore(t, e, "directory-extra-song", "歌手01", "指弹", "published", 0)
	guitarHomeScore(t, e, "directory-hidden-song", "歌手01", "指弹", "archived", 0)
	guitarHomeScore(t, e, "directory-practice-only", "只有练习", "练习", "published", 0)
	seen := map[string]bool{}
	for page := 1; page <= 2; page++ {
		status, out, _ := e.request("GET", fmt.Sprintf("guitar/artists?page=%d&pageSize=20", page), nil, nil)
		if status != 200 {
			t.Fatal(status, out)
		}
		pagination := out["pagination"].(map[string]any)
		if pagination["total"] != float64(26) || pagination["totalPages"] != float64(2) {
			t.Fatal("wrong artist totals", out)
		}
		artists := out["artists"].([]any)
		want := 20
		if page == 2 {
			want = 6
		}
		if len(artists) != want {
			t.Fatal("artist page length", len(artists), want)
		}
		for _, value := range artists {
			row := value.(map[string]any)
			id := row["id"].(string)
			if seen[id] {
				t.Fatal("artist repeated across pages", id)
			}
			seen[id] = true
			if row["name"] == "只有练习" || row["name"] == "歌手27" {
				t.Fatal("unpublished artist included", row)
			}
			songs := row["scores"].([]any)
			if len(songs) < 1 || len(songs) > 2 {
				t.Fatal("invalid representative songs", row)
			}
			for _, song := range songs {
				item := song.(map[string]any)
				if item["id"] == "directory-hidden-song" {
					t.Fatal("archived representative song", row)
				}
				if status, _, _ := e.request("GET", "guitar/scores/"+item["id"].(string), nil, nil); status != 200 {
					t.Fatal("broken song destination", item, status)
				}
			}
		}
	}
	if status, out, _ := e.request("GET", "guitar/artists?page=3&pageSize=20", nil, nil); status != 200 || len(out["artists"].([]any)) != 0 {
		t.Fatal("end of directory", status, out)
	}
	for _, query := range []string{"page=0", "pageSize=51", "page=oops"} {
		if status, _, _ := e.request("GET", "guitar/artists?"+query, nil, nil); status != 400 {
			t.Fatal(query, status)
		}
	}
	if status, _, _ := e.request("GET", "main/artists", nil, nil); status != 404 {
		t.Fatal("app isolation", status)
	}
}

func TestGuitarAllLearningStarsPaginationIsStableAndPrivate(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	for i := 1; i <= 25; i++ {
		id := fmt.Sprintf("a3410000-0000-4000-8000-%012d", i)
		if _, err := e.db.Exec(ctx, `INSERT INTO users(id,email,password_hash) VALUES($1,$2,'test-only')`, id, fmt.Sprintf("rank-%d@example.test", i)); err != nil {
			t.Fatal(err)
		}
		if _, err := e.db.Exec(ctx, `INSERT INTO user_apps(app_id,user_id) VALUES('guitar',$1)`, id); err != nil {
			t.Fatal(err)
		}
		if _, err := e.db.Exec(ctx, `INSERT INTO guitar_members(user_id,nickname,avatar) VALUES($1,$2,'leaf')`, id, fmt.Sprintf("排行榜琴友%02d", i)); err != nil {
			t.Fatal(err)
		}
		if _, err := e.db.Exec(ctx, `INSERT INTO guitar_visits(user_id,visit_date)
 SELECT $1,(now() AT TIME ZONE 'Asia/Shanghai')::date-d
 FROM generate_series(0,$2::integer) d
 WHERE (now() AT TIME ZONE 'Asia/Shanghai')::date-d>=date_trunc('week',now() AT TIME ZONE 'Asia/Shanghai')::date`, id, i%3); err != nil {
			t.Fatal(err)
		}
	}
	for _, period := range []string{"week", "month"} {
		seen := map[string]bool{}
		lastDays := 32.0
		for page := 1; page <= 2; page++ {
			status, out, _ := e.request("GET", fmt.Sprintf("guitar/learning-stars?period=%s&page=%d&pageSize=20", period, page), nil, nil)
			if status != 200 {
				t.Fatal(status, out)
			}
			pagination := out["pagination"].(map[string]any)
			if pagination["total"] != float64(25) || pagination["totalPages"] != float64(2) {
				t.Fatal("rank totals", out)
			}
			stars := out["stars"].([]any)
			want := 20
			if page == 2 {
				want = 5
			}
			if len(stars) != want {
				t.Fatal("rank page size", out)
			}
			for _, value := range stars {
				row := value.(map[string]any)
				name := row["nickname"].(string)
				if seen[name] {
					t.Fatal("rank repeated on another page", row)
				}
				seen[name] = true
				days := row["days"].(float64)
				if days > lastDays {
					t.Fatal("rank ordering", row)
				}
				lastDays = days
				for key := range row {
					if key != "nickname" && key != "avatar" && key != "avatarUrl" && key != "days" {
						t.Fatal("private field", key)
					}
				}
			}
		}
		_, top, _ := e.request("GET", "guitar/learning-stars?period="+period, nil, nil)
		if len(top["stars"].([]any)) != 3 {
			t.Fatal("homepage default must remain top three", top)
		}
	}
	for _, query := range []string{"page=0", "pageSize=51", "page=bad"} {
		if status, _, _ := e.request("GET", "guitar/learning-stars?"+query, nil, nil); status != 400 {
			t.Fatal(query, status)
		}
	}
}
