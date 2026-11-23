package main

import (
	"context"
	"testing"
)

func TestGuitarPracticeHistoryPagesPastLegacyLimit(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "long-practice")
	other := guitarTestAccount(t, e, "other-practice")
	_, account, _ := e.request("GET", "guitar/session", nil, owner)
	user := account["user"].(map[string]any)["id"].(string)
	e.sql(`INSERT INTO guitar_practice_sessions(user_id,id,record_kind,practice_day,started_at,ended_at,title,seconds)
 SELECT $1,gen_random_uuid(),'manual',current_date,now(),now(),'分页练习',60 FROM generate_series(1,107)`, user)
	_, existing, _ := e.request("GET", "guitar/workbench", nil, owner)
	if len(existing["records"].([]any)) != 100 || existing["totalSeconds"] != float64(107*60) {
		t.Fatal("the legacy workbench changed", existing["totalSeconds"])
	}
	path := "guitar/practice/history?pageSize=31"
	seen := map[string]bool{}
	for page, cursor := 0, ""; ; page++ {
		status, data, _ := e.request("GET", path+"&cursor="+cursor, nil, owner)
		if status != 200 || page > 3 {
			t.Fatalf("practice history page %d: %d %#v", page, status, data)
		}
		for _, item := range data["records"].([]any) {
			id := item.(map[string]any)["id"].(string)
			if seen[id] {
				t.Fatal("a tied history record was repeated", id)
			}
			seen[id] = true
		}
		cursor = data["nextCursor"].(string)
		if cursor == "" {
			break
		}
	}
	if len(seen) != 107 {
		t.Fatal("the paginated history dropped records", len(seen))
	}
	if status, data, _ := e.request("GET", path, nil, other); status != 200 || len(data["records"].([]any)) != 0 {
		t.Fatal("history leaked across accounts", status, data)
	}
	if status, _, _ := e.request("GET", path+"&cursor=invalid", nil, owner); status != 400 {
		t.Fatal("invalid cursor accepted", status)
	}
}

func TestGuitarMemberSettingsMergePreservesAvatarVersion(t *testing.T) {
	e := guitarTestEnvAt034(t)
	owner := guitarTestAccount(t, e, "merged-member")
	_, account, _ := e.request("GET", "guitar/session", nil, owner)
	user := account["user"].(map[string]any)["id"].(string)
	e.sql(`UPDATE guitar_members SET nickname='有头像的琴友',avatar_bytes='test',avatar_mime='image/png'
 WHERE user_id=$1`, user)
	e.sql(`INSERT INTO guitar_practice_preferences(user_id,weekly_minutes,weekly_days,first_tuned,equipped_title)
 VALUES($1,225,4,true,'songs-1')`, user)
	_, before, _ := e.request("GET", "guitar/identity", nil, owner)
	applyGuitarSchemaNext(t, e)
	_, merged, _ := e.request("GET", "guitar/workbench", nil, owner)
	if merged["weeklyMinutes"] != float64(225) || merged["weeklyDays"] != float64(4) ||
		merged["firstTuned"] != true || merged["equippedTitle"] != "songs-1" {
		t.Fatal("practice settings changed", merged)
	}
	s, after, _ := e.request("GET", "guitar/identity", nil, owner)
	if s != 200 || before["avatarUrl"] != after["avatarUrl"] || before["nickname"] != after["nickname"] {
		t.Fatal("merging settings invalidated profile data", before, after)
	}
	var frameCount int
	if err := e.db.QueryRow(context.Background(), `SELECT count(*) FROM guitar_members WHERE user_id=$1`, user).Scan(&frameCount); err != nil || frameCount != 1 {
		t.Fatal("member no longer has a single record", frameCount, err)
	}
	if current := guitarFrameProgress(18000, 9).Current; current.Level != 1 {
		t.Fatal("frame unlocked without enough practice days", current)
	}
	if current := guitarFrameProgress(18000, 10).Current; current.Level != 2 {
		t.Fatal("frame did not unlock at both thresholds", current)
	}
}
