package main

import (
	"bytes"
	"context"
	"encoding/json"
	"image"
	"image/png"
	"mime/multipart"
	"net/http"
	"testing"
)

func TestGuitarCommunityShowsThreeTopicsWithSeparatePermissions(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "request-owner")
	member := guitarTestAccount(t, e, "request-member")
	guitarHomeScore(t, e, "already-public", "琴友", "指弹", "published", 1)
	create, request, _ := e.request("POST", "guitar/score-requests", map[string]string{"title": "月光", "artist": "琴友", "note": "想找指弹版"}, owner)
	if create != 201 {
		t.Fatal(create, request)
	}
	id := request["id"].(string)
	detail := "guitar/score-requests/" + id
	_, suggestion, _ := e.request("POST", "guitar/messages", map[string]string{"body": "希望能记住看谱模式"}, nil)
	status, question, _ := e.request("POST", "guitar/community/questions", map[string]string{"title": "F 和弦怎么按", "body": "一弦总是按不响"}, member)
	if status != 201 {
		t.Fatal(status, question)
	}
	questionID := question["id"].(string)
	questionPath := "guitar/community/questions/" + questionID
	answerBody := map[string]string{"body": "试着让食指稍微侧过来。"}
	if status, _, _ := e.request("POST", questionPath+"/answers", answerBody, nil); status != 401 {
		t.Fatal(status)
	}
	if status, out, _ := e.request("POST", questionPath+"/answers", answerBody, owner); status != 201 {
		t.Fatal(status, out)
	}
	if status, _, _ := e.request("POST", "guitar/community/questions/"+suggestion["id"].(string)+"/answers", answerBody, owner); status != 404 {
		t.Fatal(status)
	}
	if status, out, _ := e.request("GET", questionPath, nil, nil); status != 200 || len(out["answers"].([]any)) != 1 {
		t.Fatal("question and reply should be public", status, out)
	}
	if status, out, _ := e.request("GET", "guitar/community", nil, nil); status != 200 || len(out["topics"].([]any)) != 3 {
		t.Fatal("all topics must share the public feed", status, out)
	}
	for _, kind := range []string{"questions", "requests", "suggestions"} {
		status, out, _ := e.request("GET", "guitar/community?kind="+kind, nil, nil)
		if status != 200 || len(out["topics"].([]any)) != 1 || out["topics"].([]any)[0].(map[string]any)["kind"] != kind {
			t.Fatal("incorrect topic filtering", kind, status, out)
		}
	}
	status, list, _ := e.request("GET", "guitar/score-requests/public", nil, nil)
	if status != 200 || len(list["requests"].([]any)) != 1 {
		t.Fatal("anonymous request listing failed", status, list)
	}
	status, list, _ = e.request("GET", "guitar/messages", nil, nil)
	if status != 200 || len(list["messages"].([]any)) != 1 || list["messages"].([]any)[0].(map[string]any)["id"] != suggestion["id"] {
		t.Fatal("requests and suggestions must be separate", status, list)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"message", id, "accepted", "不是网站建议"}); err == nil {
		t.Fatal("a request was processed as a website suggestion")
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"message", questionID, "accepted", "不是网站建议"}); err == nil {
		t.Fatal("a question was processed as a website suggestion")
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"message", suggestion["id"].(string), "accepted", "已加入计划"}); err != nil {
		t.Fatal("administrator cannot process a suggestion", err)
	}

	if s, _, _ := e.request("PUT", detail+"/solution", map[string]string{"scoreId": "already-public"}, owner); s != 404 {
		t.Fatal("cannot adopt a score that wasn't uploaded in response", s)
	}
}

func TestGuitarRequestUploadAppearsOnlyAfterApproval(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "upload-request-owner")
	contributor := guitarTestAccount(t, e, "upload-request-member")
	_, request, _ := e.request("POST", "guitar/score-requests", map[string]string{"title": "测试指弹曲"}, owner)
	id := request["id"].(string)
	detail := "guitar/score-requests/" + id
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	for k, v := range map[string]string{"title": "测试指弹曲", "category": "指弹", "difficultyStars": "2.5", "permission": "confirmed", "requestId": id} {
		if err := form.WriteField(k, v); err != nil {
			t.Fatal(err)
		}
	}
	file, err := form.CreateFormFile("file", "test.png")
	if err != nil {
		t.Fatal(err)
	}
	if err := png.Encode(file, image.NewRGBA(image.Rect(0, 0, 8, 8))); err != nil {
		t.Fatal(err)
	}
	if err := form.Close(); err != nil {
		t.Fatal(err)
	}
	post, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/guitar/uploads", &body)
	post.Header.Set("Origin", "https://notes.test")
	post.Header.Set("Content-Type", form.FormDataContentType())
	post.AddCookie(contributor)
	response, err := http.DefaultClient.Do(post)
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	err = json.NewDecoder(response.Body).Decode(&out)
	response.Body.Close()
	if err != nil || response.StatusCode != 201 {
		t.Fatal(response.StatusCode, out, err)
	}
	upload := out["id"].(string)
	_, pending, _ := e.request("GET", detail, nil, nil)
	if len(pending["answers"].([]any)) != 0 {
		t.Fatal("unreviewed answer exposed", pending)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"approve", upload}); err != nil {
		t.Fatal(err)
	}
	if status, _, _ := e.request("DELETE", "guitar/uploads/"+upload, nil, contributor); status != 200 {
		t.Fatal(status)
	}
	_, approved, _ := e.request("GET", detail, nil, nil)
	answers := approved["answers"].([]any)
	if len(answers) != 1 || answers[0].(map[string]any)["scoreId"] != "upload-"+upload {
		t.Fatal("reviewed response must survive deletion of the personal upload", approved)
	}
	answer := map[string]string{"scoreId": "upload-" + upload}
	if status, _, _ := e.request("PUT", detail+"/solution", answer, contributor); status != 403 {
		t.Fatal(status)
	}
	for i := 0; i < 2; i++ {
		if status, out, _ := e.request("PUT", detail+"/solution", answer, owner); status != 200 {
			t.Fatal(status, out)
		}
	}
	_, resolved, _ := e.request("GET", detail, nil, nil)
	summary := resolved["request"].(map[string]any)
	if summary["status"] != "solved" || summary["solutionScoreId"] != answer["scoreId"] || summary["canResolve"] != false {
		t.Fatal("public solution state or permissions incorrect", resolved)
	}
}
