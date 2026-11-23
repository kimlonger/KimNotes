package main

import (
	"context"
	"testing"
)

func TestGuitarRequestCategoryCanonicalizesLegacyClients(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "request-category-owner")

	create := func(title, category, note string) string {
		t.Helper()
		body := map[string]string{"title": title, "note": note}
		if category != "" {
			body["category"] = category
		}
		status, result, _ := e.request("POST", "guitar/score-requests", body, owner)
		if status != 201 {
			t.Fatalf("create %s: %d %v", title, status, result)
		}
		return result["id"].(string)
	}
	check := func(id, wantCategory, wantNote string) {
		t.Helper()
		status, result, _ := e.request("GET", "guitar/score-requests/"+id, nil, nil)
		if status != 200 {
			t.Fatalf("read %s: %d %v", id, status, result)
		}
		request := result["request"].(map[string]any)
		if request["category"] != wantCategory || request["note"] != wantNote {
			t.Fatalf("request %s: got %v, want category %q, note %q", id, request, wantCategory, wantNote)
		}
	}

	before := create("迁移前", "弹唱", "希望简单一点")
	legacy := create("旧客户端", "", "类型：指弹\n原来的说明")
	unknown := create("普通旧记录", "", "想找指弹版本")
	nearMiss := create("未识别首行", "", "类型：弹唱版\n保留原文")
	check(before, "弹唱", "希望简单一点")
	check(legacy, "指弹", "原来的说明")
	check(unknown, "", "想找指弹版本")
	check(nearMiss, "", "类型：弹唱版\n保留原文")
	if status, result, _ := e.request("POST", "guitar/score-requests", map[string]string{"title": "错误类型", "category": "练习"}, owner); status != 400 || result["code"] != "invalid_category" {
		t.Fatalf("invalid category: %d %v", status, result)
	}
	check(before, "弹唱", "希望简单一点")
	check(legacy, "指弹", "原来的说明")
	after := create("迁移后", "指弹", "指定指弹")
	check(after, "指弹", "指定指弹")
	var stored, storedNote string
	if err := e.db.QueryRow(context.Background(), `SELECT requested_category,body FROM guitar_messages WHERE id=$1`, after).Scan(&stored, &storedNote); err != nil || stored != "指弹" || storedNote != "指定指弹" {
		t.Fatalf("canonical category and note: %q %q %v", stored, storedNote, err)
	}
	for _, endpoint := range []string{"guitar/score-requests/public", "guitar/community?kind=requests"} {
		status, result, _ := e.request("GET", endpoint, nil, nil)
		if status != 200 {
			t.Fatalf("%s: %d %v", endpoint, status, result)
		}
		key := "requests"
		if endpoint == "guitar/community?kind=requests" {
			key = "topics"
		}
		found := false
		for _, value := range result[key].([]any) {
			item := value.(map[string]any)
			if item["id"] == after {
				found = true
				if item["category"] != "指弹" {
					t.Fatalf("%s category: %v", endpoint, item)
				}
				textKey := "note"
				if key == "topics" {
					textKey = "body"
				}
				if item[textKey] != "指定指弹" {
					t.Fatalf("%s note: %v", endpoint, item)
				}
			}
		}
		if !found {
			t.Fatalf("%s missing %s", endpoint, after)
		}
	}
}
