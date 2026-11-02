package main

import (
	"bytes"
	"context"
	"encoding/json"
	"image"
	"image/png"
	"io"
	"mime/multipart"
	"net/http"
	"strings"
	"testing"
)

func guitarTestEnv(t *testing.T) *testEnv {
	t.Helper()
	e := newTestEnv(t)
	if _, err := e.db.Exec(context.Background(), guitarSchema); err != nil {
		t.Fatal(err)
	}
	if err := importGuitar(context.Background(), e.app, guitarSeed); err != nil {
		t.Fatal(err)
	}
	return e
}
func guitarTestAccount(t *testing.T, e *testEnv, name string) *http.Cookie {
	t.Helper()
	status, out, resp := e.request("POST", "guitar/auth/register", credentials{Email: name + "@example.com", Password: "Test-password-123"}, nil)
	if status != 200 {
		t.Fatal(status, out)
	}
	for _, c := range resp.Cookies() {
		if c.Name == e.app.cookieName("guitar") {
			return c
		}
	}
	t.Fatal("guitar cookie missing")
	return nil
}
func TestGuitarSharedAccountAndHierarchy(t *testing.T) {
	e := guitarTestEnv(t)
	notes := e.account("shared-guitar")
	status, old, _ := e.request("GET", "kimnotes/auth/me", nil, notes)
	if status != 200 {
		t.Fatal(status)
	}
	status, _, resp := e.request("POST", "guitar/auth/login", credentials{Email: "shared-guitar@example.com", Password: "Test-password-123"}, nil)
	if status != 200 {
		t.Fatal(status)
	}
	var guitar *http.Cookie
	for _, c := range resp.Cookies() {
		if c.Name == e.app.cookieName("guitar") {
			guitar = c
		}
	}
	status, now, _ := e.request("GET", "guitar/auth/me", nil, guitar)
	if status != 200 || old["user"].(map[string]any)["id"] != now["user"].(map[string]any)["id"] {
		t.Fatal("identity was not reused", status, now)
	}
	for _, p := range []string{"guitar/scores", "guitar/lessons/beginner-01", "guitar/profile"} {
		if s, _, _ := e.request("GET", p, nil, notes); s != 401 {
			t.Fatal("notes session used for guitar", p, s)
		}
	}
	if s, _, _ := e.request("GET", "guitar/courses", nil, nil); s != 200 {
		t.Fatal("course catalog", s)
	}
	s, c, _ := e.request("GET", "guitar/courses/beginner", nil, nil)
	if s != 200 || len(c["chapters"].([]any)) != 6 || c["course"].(map[string]any)["lessonCount"] != float64(24) {
		t.Fatal("course hierarchy", s, c)
	}
	if s, _, _ := e.request("GET", "guitar/lessons/beginner-01", nil, guitar); s != 200 {
		t.Fatal("lesson", s)
	}
	if s, _, _ := e.request("GET", "main/courses", nil, nil); s != 404 {
		t.Fatal("guitar content exposed as another app", s)
	}
	if s, _, _ := e.request("PUT", "guitar/favorites/course/intermediate", nil, guitar); s != 404 {
		t.Fatal("planned course was treated as published", s)
	}
}
func TestGuitarPrivateProgressFavoritesAndVisits(t *testing.T) {
	e := guitarTestEnv(t)
	one, two := guitarTestAccount(t, e, "guitar-one"), guitarTestAccount(t, e, "guitar-two")
	if s, o, _ := e.request("PUT", "guitar/lessons/beginner-03/progress", map[string]any{"completed": true, "checks": []string{"已复查"}}, one); s != 200 {
		t.Fatal(s, o)
	}
	if s, _, _ := e.request("POST", "guitar/lessons/beginner-03/open", nil, one); s != 200 {
		t.Fatal(s)
	}
	if s, _, _ := e.request("PUT", "guitar/favorites/score/single-string", nil, one); s != 200 {
		t.Fatal(s)
	}
	for i := 0; i < 3; i++ {
		s, p, _ := e.request("POST", "guitar/visits", nil, one)
		if s != 200 || p["totalDays"] != float64(1) {
			t.Fatal("daily open double counted", s, p)
		}
		if !p["progress"].([]any)[0].(map[string]any)["completed"].(bool) {
			t.Fatal("opening lesson erased completion")
		}
	}
	if s, p, _ := e.request("GET", "guitar/profile", nil, two); s != 200 || len(p["progress"].([]any)) != 0 || p["totalDays"] != float64(0) {
		t.Fatal("another user's progress leaked", s, p)
	}
	if s, f, _ := e.request("GET", "guitar/favorites", nil, two); s != 200 || len(f["favorites"].([]any)) != 0 {
		t.Fatal("another user's favorites leaked", s, f)
	}
	if s, _, _ := e.request("PUT", "guitar/lessons/missing/progress", map[string]any{"completed": true, "checks": []string{}}, one); s != 404 {
		t.Fatal("nonexistent progress accepted", s)
	}
	if s, _, _ := e.request("PUT", "guitar/lessons/beginner-03/progress", map[string]any{"completed": true, "userId": "other"}, one); s != 400 {
		t.Fatal("user ID input accepted", s)
	}
}
func TestGuitarAnonymousFeedbackAndContextualQuestions(t *testing.T) {
	e := guitarTestEnv(t)
	one := guitarTestAccount(t, e, "questioner")
	s, _, _ := e.request("POST", "guitar/messages", map[string]string{"nickname": "", "body": "希望增加节奏练习"}, nil)
	if s != 201 {
		t.Fatal("anonymous feedback", s)
	}
	s, m, _ := e.request("GET", "guitar/messages", nil, nil)
	if s != 200 || m["messages"].([]any)[0].(map[string]any)["nickname"] != "匿名琴友" {
		t.Fatal(s, m)
	}
	messageID := m["messages"].([]any)[0].(map[string]any)["id"].(string)
	if err := reviewGuitar(context.Background(), e.db, []string{"message", messageID, "accepted", "已加入需求清单"}); err != nil {
		t.Fatal(err)
	}
	_, m, _ = e.request("GET", "guitar/messages", nil, nil)
	if m["messages"].([]any)[0].(map[string]any)["reply"] != "已加入需求清单" {
		t.Fatal("operator reply missing", m)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"message", messageID, "hidden", ""}); err != nil {
		t.Fatal(err)
	}
	_, m, _ = e.request("GET", "guitar/messages", nil, nil)
	if len(m["messages"].([]any)) != 0 {
		t.Fatal("hidden feedback still public", m)
	}
	if s, _, _ := e.request("POST", "guitar/courses/beginner/questions", map[string]any{"body": "如何练习？"}, nil); s != 401 {
		t.Fatal("anonymous learning post", s)
	}
	if s, _, _ := e.request("POST", "guitar/courses/intermediate/questions", map[string]any{"body": "如何练习？", "lessonId": "beginner-03"}, one); s != 400 {
		t.Fatal("cross-course context accepted", s)
	}
	s, q, _ := e.request("POST", "guitar/courses/beginner/questions", map[string]any{"body": "如何逐弦调音？", "lessonId": "beginner-03"}, one)
	if s != 201 {
		t.Fatal(s, q)
	}
	if s, _, _ := e.request("POST", "guitar/questions/"+q["id"].(string)+"/replies", map[string]string{"body": "先确认弦号。"}, one); s != 201 {
		t.Fatal("reply", s)
	}
	s, qs, _ := e.request("GET", "guitar/courses/beginner/questions", nil, nil)
	if s != 200 || len(qs["questions"].([]any)[0].(map[string]any)["replies"].([]any)) != 1 {
		t.Fatal("thread", s, qs)
	}
	raw, _ := json.Marshal(qs)
	if strings.Contains(string(raw), "questioner@example.com") {
		t.Fatal("private email exposed in public questions")
	}
}
func TestGuitarPublishedUploadSurvivesPersonalRemoval(t *testing.T) {
	e := guitarTestEnv(t)
	one, two := guitarTestAccount(t, e, "uploader"), guitarTestAccount(t, e, "reader")
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	for k, v := range map[string]string{"title": "个人原创练习", "description": "测试上传", "category": "练习", "difficulty": "入门", "permission": "confirmed"} {
		form.WriteField(k, v)
	}
	f, _ := form.CreateFormFile("file", "practice.png")
	png.Encode(f, image.NewRGBA(image.Rect(0, 0, 8, 8)))
	form.Close()
	request, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/guitar/uploads", &body)
	request.Header.Set("Origin", "https://notes.test")
	request.Header.Set("Content-Type", form.FormDataContentType())
	request.AddCookie(one)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	json.NewDecoder(response.Body).Decode(&out)
	response.Body.Close()
	if response.StatusCode != 201 {
		t.Fatal(response.StatusCode, out)
	}
	id := out["id"].(string)
	score := "upload-" + id
	if s, _, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 404 {
		t.Fatal("unreviewed upload exposed", s)
	}
	if err = reviewGuitar(context.Background(), e.db, []string{"approve", id}); err != nil {
		t.Fatal(err)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, two); s != 404 {
		t.Fatal("another user removed upload", s)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, one); s != 200 {
		t.Fatal("remove record", s)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 200 {
		t.Fatal("public resource was removed with personal record", s)
	}
	req, _ := http.NewRequest("GET", e.server.URL+"/api/v1/apps/guitar/scores/"+score+"/file", nil)
	req.AddCookie(two)
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	data, _ := io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || len(data) == 0 || res.Header.Get("Content-Type") != "image/png" {
		t.Fatal("public bytes were lost")
	}
}
func TestGuitarSeedAndRank(t *testing.T) {
	e := guitarTestEnv(t)
	if err := importGuitar(context.Background(), e.app, guitarSeed); err != nil {
		t.Fatal(err)
	}
	var n int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM guitar_lessons").Scan(&n); err != nil || n != 24 {
		t.Fatal("seed not idempotent", n, err)
	}
	for _, c := range []struct {
		days  int
		title string
	}{{0, "吉他小白"}, {7, "入门琴友"}, {30, "吉他新秀"}, {365, "吉他大师"}} {
		title, _, _ := guitarRank(c.days)
		if title != c.title {
			t.Fatal(c, title)
		}
	}
}

func TestGuitarCourseUpdatesPreservePersonalProgress(t *testing.T) {
	e := guitarTestEnv(t)
	cookie := guitarTestAccount(t, e, "course-editor")
	if s, _, _ := e.request("PUT", "guitar/lessons/beginner-01/progress", map[string]any{"completed": true, "checks": []string{}}, cookie); s != 200 {
		t.Fatal(s)
	}
	var seed map[string]any
	if err := json.Unmarshal(guitarSeed, &seed); err != nil {
		t.Fatal(err)
	}
	course := seed["courses"].([]any)[0].(map[string]any)
	course["title"] = "修订后的初级教程"
	raw, _ := json.Marshal(seed)
	ctx := context.Background()
	if err := importGuitar(ctx, e.app, raw); err != nil {
		t.Fatal(err)
	}
	_, before, _ := e.request("GET", "guitar/courses/beginner", nil, nil)
	if before["course"].(map[string]any)["title"] == course["title"] {
		t.Fatal("default import overwrote existing content")
	}
	if err := importGuitarContent(ctx, e.app, raw, true); err != nil {
		t.Fatal(err)
	}
	_, after, _ := e.request("GET", "guitar/courses/beginner", nil, nil)
	if after["course"].(map[string]any)["title"] != course["title"] {
		t.Fatal("explicit replacement failed")
	}
	_, profile, _ := e.request("GET", "guitar/profile", nil, cookie)
	if !profile["progress"].([]any)[0].(map[string]any)["completed"].(bool) {
		t.Fatal("course update lost personal progress")
	}
	chapters := course["chapters"].([]any)
	chapters[1].(map[string]any)["lessons"] = chapters[0].(map[string]any)["lessons"]
	invalid, _ := json.Marshal(seed)
	if err := importGuitarContent(ctx, e.app, invalid, true); err == nil {
		t.Fatal("lesson ID silently reused under another chapter")
	}
}
