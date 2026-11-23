package main

import (
	"context"
	"sync"
	"testing"
)

func TestGuitarPracticeMigrationPreservesEveryHistoricalEvent(t *testing.T) {
	e := guitarTestEnvBeforePracticeUnification(t)
	ctx := context.Background()
	owner := guitarTestAccount(t, e, "practice-migration-owner")
	other := guitarTestAccount(t, e, "practice-migration-other")
	_, account, _ := e.request("GET", "guitar/session", nil, owner)
	user := account["user"].(map[string]any)["id"].(string)
	_, account, _ = e.request("GET", "guitar/session", nil, other)
	otherUser := account["user"].(map[string]any)["id"].(string)
	sharedID, timedTask, estimatedTask, scoreTask, removedTask := newID(), newID(), newID(), newID(), newID()
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_tasks(user_id,id,day,title,minutes,position,done) VALUES
 ($1,$2,'2026-01-02','实际计时',30,1,true),($1,$3,'2026-01-03','历史估算',15,2,true),
 ($1,$4,'2026-01-04','歌曲弹唱',20,3,true)`, user, timedTask, estimatedTask, scoreTask); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_sessions(user_id,id,score_id,task_id,started_at,ended_at,seconds)
 VALUES($1,$2,'course-little-bee',$3,'2026-01-04T01:02:03Z','2026-01-04T01:03:03Z',60)`, user, sharedID, scoreTask); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_logs(user_id,id,day,title,minutes,note)
 VALUES($1,$2,'2026-01-01','手工练习',10,'原备注')`, user, sharedID); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_practice_task_sessions(user_id,id,task_id,day,title,seconds,recorded_at) VALUES
 ($1,$2,$3,'2026-01-02','实际计时',90,'2026-01-02T12:00:00Z'),
 ($1,$4,$5,'2026-01-05','已删除的清单',30,'2026-01-05T12:00:00Z'),
 ($6,$2,$7,'2026-01-06','其他账号',15,'2026-01-06T12:00:00Z')`,
		user, sharedID, timedTask, newID(), removedTask, otherUser, newID()); err != nil {
		t.Fatal(err)
	}
	const legacyEvents = `
 SELECT p.user_id,p.id::text,p.score_id,p.task_id,(p.started_at AT TIME ZONE 'Asia/Shanghai')::date AS day,
 p.seconds,'score' AS kind,''::text AS note,s.title,p.started_at AS at
 FROM guitar_practice_sessions p JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id
 WHERE p.ended_at IS NOT NULL AND p.seconds>0
 UNION ALL SELECT user_id,id::text,NULL,NULL,day,minutes*60,'manual',note,title,NULL FROM guitar_practice_logs
 UNION ALL SELECT user_id,id::text,NULL,task_id,day,seconds,'task','',title,recorded_at FROM guitar_practice_task_sessions
 UNION ALL SELECT t.user_id,t.id::text,NULL,NULL,t.day,t.minutes*60,'task','',t.title,NULL FROM guitar_practice_tasks t
 WHERE t.done AND t.minutes>0
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_task_sessions p WHERE p.user_id=t.user_id AND p.task_id=t.id)
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_sessions p WHERE p.user_id=t.user_id AND p.task_id=t.id AND p.ended_at IS NOT NULL AND p.seconds>0)`
	readEvents := func(query string) string {
		t.Helper()
		var data []byte
		if err := e.db.QueryRow(ctx, `SELECT jsonb_agg(to_jsonb(e) ORDER BY user_id,kind,id) FROM
 (SELECT user_id,id,score_id,task_id,day,seconds,kind,note,title,at FROM (`+query+`) all_events) e`).Scan(&data); err != nil {
			t.Fatal(err)
		}
		return string(data)
	}
	before := readEvents(legacyEvents)
	if _, err := e.db.Exec(ctx, migrationFixture("030_guitar_practice_unified")); err != nil {
		t.Fatal(err)
	}
	if after := readEvents(guitarPracticeEvents); after != before {
		t.Fatalf("historical IDs, accounts, dates, titles, notes or durations changed\nbefore %s\nafter  %s", before, after)
	}
	var oldTablesGone bool
	if err := e.db.QueryRow(ctx, `SELECT to_regclass('guitar_practice_logs') IS NULL
 AND to_regclass('guitar_practice_task_sessions') IS NULL`).Scan(&oldTablesGone); err != nil || !oldTablesGone {
		t.Fatal("old practice sources remain", oldTablesGone, err)
	}
	var collisions, ownerSeconds int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FROM guitar_practice_sessions WHERE id=$1`, sharedID).Scan(&collisions); err != nil || collisions != 4 {
		t.Fatal("source/account ID collisions were lost", collisions, err)
	}
	if err := e.db.QueryRow(ctx, `SELECT sum(seconds) FROM guitar_practice_sessions WHERE user_id=$1`, user).Scan(&ownerSeconds); err != nil || ownerSeconds != 1680 {
		t.Fatal("historical total changed", ownerSeconds, err)
	}
}

func TestGuitarDeletedTaskAcceptsFirstDelayedTimerAndRetries(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "delayed-task-owner")
	other := guitarTestAccount(t, e, "delayed-task-other")
	const path = "guitar/workbench"
	status, data, _ := e.request("PATCH", path, map[string]any{"action": "add-task", "title": "离线练习", "minutes": 20}, owner)
	if status != 200 {
		t.Fatal(status, data)
	}
	id := data["tasks"].([]any)[0].(map[string]any)["id"].(string)
	_, data, _ = e.request("PATCH", path, map[string]any{"action": "toggle-task", "id": id, "done": true}, owner)
	if data["totalSeconds"] != float64(1200) {
		t.Fatal("installed-client check-off estimate changed", data)
	}
	_, data, _ = e.request("PATCH", path, map[string]any{"action": "remove-task", "id": id}, owner)
	if len(data["tasks"].([]any)) != 0 || data["totalSeconds"] != float64(0) {
		t.Fatal("removed checklist estimate should disappear", data)
	}
	write := map[string]any{"action": "record-task-practice", "id": id, "sessionId": newID(), "seconds": 59}
	if status, out, _ := e.request("PATCH", path, write, other); status != 404 {
		t.Fatal("removed task accepted another account", status, out)
	}
	status, data, _ = e.request("PATCH", path, write, owner)
	if status != 200 || len(data["tasks"].([]any)) != 0 || data["totalSeconds"] != float64(59) {
		t.Fatal("first delayed write was lost or resurrected the checklist", status, data)
	}
	write["seconds"] = 700
	status, data, _ = e.request("PATCH", path, write, owner)
	if status != 200 || data["totalSeconds"] != float64(59) || len(data["records"].([]any)) != 1 {
		t.Fatal("retry counted twice or rewrote duration", status, data)
	}
	record := data["records"].([]any)[0].(map[string]any)
	if record["id"] != write["sessionId"] || record["title"] != "离线练习" || record["kind"] != "task" {
		t.Fatal("timer history wire format changed", record)
	}
	status, overview, _ := e.request("GET", "guitar/practice", nil, owner)
	if status != 200 || overview["totalSeconds"] != float64(0) || len(overview["recent"].([]any)) != 0 {
		t.Fatal("task timing leaked into score-only practice API", status, overview)
	}
	if _, err := e.db.Exec(context.Background(), `DELETE FROM guitar_practice_tasks WHERE id=$1`, id); err != nil {
		t.Fatal(err)
	}
	if status, data, _ := e.request("PATCH", path, write, owner); status != 200 || data["totalSeconds"] != float64(59) {
		t.Fatal("retained timer retry must survive historical hard deletion", status, data)
	}
}

func TestGuitarPracticeSourcesCannotBeChangedThroughAnotherWireAPI(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "practice-source-owner")
	_, data, _ := e.request("GET", "guitar/workbench", nil, owner)
	status, data, _ := e.request("PATCH", "guitar/workbench", map[string]any{
		"action": "save-log", "title": "手工练习", "day": data["today"], "minutes": 15, "note": "原备注",
	}, owner)
	if status != 200 {
		t.Fatal(status, data)
	}
	id := data["records"].([]any)[0].(map[string]any)["id"].(string)
	if status, out, _ := e.request("PUT", "guitar/practice/"+id, map[string]any{"activeSeconds": 1}, owner); status == 200 {
		t.Fatal("score stop modified a manual record", out)
	}
	status, data, _ = e.request("PATCH", "guitar/workbench", map[string]any{
		"action": "save-log", "id": id, "title": "改后的手工练习", "day": data["today"], "minutes": 16, "note": "新备注",
	}, owner)
	if status != 200 || data["totalSeconds"] != float64(960) {
		t.Fatal("manual update changed its API behavior", status, data)
	}
	status, data, _ = e.request("PATCH", "guitar/workbench", map[string]any{"action": "remove-log", "id": id}, owner)
	if status != 200 || data["totalSeconds"] != float64(0) {
		t.Fatal("manual removal changed its API behavior", status, data)
	}
}

func TestGuitarTaskEstimateAndActualTimingSerialize(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "practice-race-owner")
	_, data, _ := e.request("PATCH", "guitar/workbench", map[string]any{"action": "add-task", "title": "并发练习", "minutes": 20}, owner)
	id := data["tasks"].([]any)[0].(map[string]any)["id"]
	sessionID := newID()
	var workers sync.WaitGroup
	results := make(chan int, 8)
	for i := 0; i < 4; i++ {
		workers.Add(2)
		go func() {
			defer workers.Done()
			status, _, _ := e.request("PATCH", "guitar/workbench", map[string]any{"action": "toggle-task", "id": id, "done": true}, owner)
			results <- status
		}()
		go func() {
			defer workers.Done()
			status, _, _ := e.request("PATCH", "guitar/workbench", map[string]any{"action": "record-task-practice", "id": id, "sessionId": sessionID, "seconds": 75}, owner)
			results <- status
		}()
	}
	workers.Wait()
	close(results)
	for status := range results {
		if status != 200 {
			t.Fatal("concurrent practice action failed", status)
		}
	}
	status, data, _ := e.request("GET", "guitar/workbench", nil, owner)
	if status != 200 || data["totalSeconds"] != float64(75) || len(data["records"].([]any)) != 1 {
		t.Fatal("estimate or retry duplicated actual timing", status, data)
	}
}
