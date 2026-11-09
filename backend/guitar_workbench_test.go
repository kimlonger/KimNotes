package main

import (
	"context"
	"testing"
)

func TestGuitarWorkbenchUsesAccountOwnedPractice(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "workbench-owner")
	other := guitarTestAccount(t, e, "workbench-other")
	const path = "guitar/workbench"
	change := func(body map[string]any) map[string]any {
		t.Helper()
		status, data, _ := e.request("PATCH", path, body, owner)
		if status != 200 {
			t.Fatal(status, data)
		}
		return data
	}
	data := change(map[string]any{"action": "add-task", "title": "爬格子", "minutes": 15})
	task := data["tasks"].([]any)[0].(map[string]any)["id"]
	for i := 0; i < 2; i++ {
		data = change(map[string]any{"action": "toggle-task", "id": task, "done": true})
		if data["totalSeconds"] != float64(900) || data["checkinDays"] != float64(1) {
			t.Fatal("task should be counted once", data)
		}
	}
	_, otherData, _ := e.request("PATCH", path, map[string]any{"action": "toggle-task", "id": task, "done": false}, other)
	if len(otherData["tasks"].([]any)) != 0 || otherData["totalSeconds"] != float64(0) {
		t.Fatal("another account saw the task", otherData)
	}
	_, data, _ = e.request("GET", path, nil, owner)
	if data["totalSeconds"] != float64(900) {
		t.Fatal("another account changed the task", data)
	}
	status, _, _ := e.request("PATCH", "guitar/scores/course-little-bee/reading", map[string]any{"page": 2, "pageCount": 3}, owner)
	if status != 200 {
		t.Fatal(status)
	}
	status, _, _ = e.request("PATCH", "guitar/scores/course-little-bee/reading", map[string]any{"learned": true}, owner)
	if status != 200 {
		t.Fatal(status)
	}
	_, data, _ = e.request("GET", path, nil, owner)
	progress := data["progress"].([]any)[0].(map[string]any)
	if progress["page"] != float64(2) || progress["learned"] != true {
		t.Fatal("marking learned changed the resume page", data)
	}
	status, data, _ = e.request("POST", "guitar/practice", map[string]any{"scoreId": "course-little-bee", "automatic": true}, owner)
	if status != 201 {
		t.Fatal(status, data)
	}
	id := data["id"].(string)
	if _, err := e.db.Exec(context.Background(), `UPDATE guitar_practice_sessions SET started_at=now()-interval '10 minutes' WHERE id=$1`, id); err != nil {
		t.Fatal(err)
	}
	_, data, _ = e.request("PUT", "guitar/practice/"+id, map[string]int{"activeSeconds": 75}, owner)
	if data["seconds"] != float64(75) {
		t.Fatal("hidden time must not be counted", data)
	}
	status, data, _ = e.request("POST", "guitar/score-requests", map[string]any{"title": "想学的歌", "artist": "歌手"}, owner)
	if status != 201 {
		t.Fatal(status, data)
	}
	_, data, _ = e.request("GET", "guitar/score-requests", nil, owner)
	if len(data["requests"].([]any)) != 1 {
		t.Fatal("owner cannot find their request", data)
	}
	_, data, _ = e.request("GET", "guitar/score-requests", nil, other)
	if len(data["requests"].([]any)) != 0 {
		t.Fatal("score request crossed accounts", data)
	}
}

func TestGuitarRepertoireUsesPublishedSongDifficulty(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "repertoire-owner")
	guitarHomeScore(t, e, "repertoire-song", "琴友", "指弹", "published", 1)
	guitarHomeScore(t, e, "repertoire-exercise", "琴友", "练习", "published", 1)
	guitarHomeScore(t, e, "repertoire-archived", "琴友", "弹唱", "archived", 1)
	status, data, _ := e.request("PATCH", "guitar/scores/repertoire-song/reading", map[string]bool{"learned": true}, owner)
	if status != 200 {
		t.Fatal(status, data)
	}
	status, data, _ = e.request("GET", "guitar/workbench", nil, owner)
	if status != 200 {
		t.Fatal(status, data)
	}
	progress := data["progress"].([]any)[0].(map[string]any)
	if progress["learned"] != true || progress["difficultyStars"] != float64(2.5) {
		t.Fatal("learned score should retain its catalog difficulty", progress)
	}
	status, data, _ = e.request("GET", "guitar/scores?q=repertoire&repertoireStars=2.5", nil, nil)
	if status != 200 {
		t.Fatal(status, data)
	}
	scores := data["scores"].([]any)
	if len(scores) != 1 || scores[0].(map[string]any)["id"] != "repertoire-song" {
		t.Fatal("only published songs that meet the goal should be listed", scores)
	}
}

func TestGuitarEquippedTitleIsOwnedAndExclusive(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "title-owner")
	other := guitarTestAccount(t, e, "title-other")
	const path = "guitar/workbench"
	change := func(id string, want int) {
		t.Helper()
		status, data, _ := e.request("PATCH", path, map[string]string{"action": "equip-title", "titleId": id}, owner)
		if status != want || (status == 200 && data["equippedTitle"] != id) {
			t.Fatal(id, status, data)
		}
	}
	change("songs-8", 400)
	change("songs-2", 403)
	change("time-1", 403)
	change("songs-1", 200)
	guitarHomeScore(t, e, "first-title-song", "测试歌手", "指弹", "published", 1)
	if status, data, _ := e.request("PATCH", "guitar/scores/first-title-song/reading", map[string]bool{"learned": true}, owner); status != 200 {
		t.Fatal(status, data)
	}
	change("songs-2", 200)
	_, data, _ := e.request("GET", path, nil, owner)
	if status, out, _ := e.request("PATCH", path, map[string]any{"action": "save-log", "title": "练琴", "minutes": 15, "day": data["today"]}, owner); status != 200 {
		t.Fatal(status, out)
	}
	change("time-2", 403)
	change("time-1", 200)
	if status, out, _ := e.request("GET", path, nil, owner); status != 200 || out["equippedTitle"] != "time-1" {
		t.Fatal(status, out)
	}
	if status, out, _ := e.request("GET", path, nil, other); status != 200 || out["equippedTitle"] != "" {
		t.Fatal(status, out)
	}
	change("", 200)
}
