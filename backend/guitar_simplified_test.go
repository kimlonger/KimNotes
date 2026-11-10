package main

import (
	"context"
	"fmt"
	"testing"
)

func TestGuitarHalfStarValidation(t *testing.T) {
	for _, raw := range []string{"0.5", "1", "1.5", "2.5", "4.5", "5"} {
		if _, err := guitarStars(raw); err != nil {
			t.Fatal(raw, err)
		}
	}
	for _, raw := range []string{"0", "0.49", "1.25", "5.01", "5.5", "NaN", "Infinity", "-1", "abc"} {
		if _, err := guitarStars(raw); err == nil {
			t.Fatal("invalid star rating accepted", raw)
		}
	}
}

func TestGuitarStarCatalogAndDatabasePrecision(t *testing.T) {
	e := guitarTestEnv(t)
	_, err := e.db.Exec(context.Background(), `INSERT INTO guitar_scores(id,title,category,difficulty,difficulty_stars,credit,notation) SELECT 'half-star-test','半星测试','指弹','初级',2.5,'test',notation FROM guitar_scores WHERE id='course-little-bee'`)
	if err != nil {
		t.Fatal(err)
	}
	s, out, _ := e.request("GET", "guitar/scores?difficultyStars=2.5&pageSize=1", nil, nil)
	if s != 200 || out["pagination"].(map[string]any)["total"] != float64(1) || out["scores"].([]any)[0].(map[string]any)["difficultyStars"] != 2.5 {
		t.Fatal(s, out)
	}
	_, out, _ = e.request("GET", "guitar/scores/half-star-test", nil, nil)
	if out["difficultyStars"] != 2.5 {
		t.Fatal(out)
	}
	for _, raw := range []string{"0.49", "1.25", "5.01", "NaN"} {
		if s, _, _ := e.request("GET", "guitar/scores?difficultyStars="+raw, nil, nil); s != 400 {
			t.Fatal(raw, s)
		}
		if _, err = e.db.Exec(context.Background(), `UPDATE guitar_scores SET difficulty_stars=$1::numeric WHERE id='half-star-test'`, raw); err == nil {
			t.Fatal("DB accepted invalid precision", raw)
		}
	}
}

func TestGuitarMessagesPriorityAndPagination(t *testing.T) {
	e := guitarTestEnv(t)
	_, err := e.db.Exec(context.Background(), `INSERT INTO guitar_messages(id,nickname,body,status,created_at)
 SELECT format('00000000-0000-4000-8000-%s',lpad(i::text,12,'0'))::uuid,'匿名琴友','pagination fixture '||i,
 CASE WHEN i%3=0 THEN 'accepted' ELSE 'open' END,timestamptz '2026-01-01 00:00:00+08'+i*interval '1 second'
 FROM generate_series(1,107) i`)
	if err != nil {
		t.Fatal(err)
	}
	_, err = e.db.Exec(context.Background(), `INSERT INTO guitar_messages(id,nickname,body,status) VALUES('00000000-0000-4000-8000-999999999999','hidden','must not leak','hidden')`)
	if err != nil {
		t.Fatal(err)
	}
	seen := map[string]bool{}
	accepted := false
	lastDate := ""
	for page := 1; page <= 6; page++ {
		s, out, _ := e.request("GET", fmt.Sprintf("guitar/messages?page=%d&pageSize=20", page), nil, nil)
		if s != 200 {
			t.Fatal(s, out)
		}
		p := out["pagination"].(map[string]any)
		if p["total"] != float64(107) || p["totalPages"] != float64(6) {
			t.Fatal(p)
		}
		rows := out["messages"].([]any)
		if (page < 6 && len(rows) != 20) || (page == 6 && len(rows) != 7) {
			t.Fatal(page, len(rows))
		}
		for _, v := range rows {
			m := v.(map[string]any)
			id := m["id"].(string)
			if seen[id] || m["status"] == "hidden" {
				t.Fatal("duplicated or hidden", m)
			}
			seen[id] = true
			if m["status"] != "open" && !accepted {
				accepted = true
				lastDate = ""
			}
			if accepted && m["status"] == "open" {
				t.Fatal("open message sorted after accepted", m)
			}
			date := m["createdAt"].(string)
			if lastDate != "" && date > lastDate {
				t.Fatal("not newest first", m)
			}
			lastDate = date
		}
	}
	if len(seen) != 107 {
		t.Fatal("pagination truncated", len(seen))
	}
	for _, query := range []string{"page=0", "page=-1", "pageSize=51", "pageSize=oops"} {
		if s, _, _ := e.request("GET", "guitar/messages?"+query, nil, nil); s != 400 {
			t.Fatal(query, s)
		}
	}
}
