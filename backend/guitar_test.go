package main

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"image"
	"image/png"
	"io"
	"mime/multipart"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func publishedGuitarTestSeed(t *testing.T) []byte {
	t.Helper()
	var seed map[string]any
	if err := json.Unmarshal(guitarSeed, &seed); err != nil {
		t.Fatal(err)
	}
	for _, item := range seed["courses"].([]any) {
		course := item.(map[string]any)
		if course["id"] == "beginner-book" {
			course["status"] = "published"
		}
	}
	raw, err := json.Marshal(seed)
	if err != nil {
		t.Fatal(err)
	}
	return raw
}
func guitarTestEnv(t *testing.T) *testEnv {
	t.Helper()
	e := newTestEnv(t)
	if _, err := e.db.Exec(context.Background(), guitarSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarCatalogSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarOriginSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarExperienceSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarPagesSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarStarsSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarHomeSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarWorkbenchSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarRequestAnswerSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarCommunityQuestionSchema); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(context.Background(), guitarEquippedTitleSchema); err != nil {
		t.Fatal(err)
	}
	for _, schema := range []string{guitarScoreAttributesSchema, guitarArtistPhotosSchema} {
		if _, err := e.db.Exec(context.Background(), schema); err != nil {
			t.Fatal(err)
		}
	}
	if err := importGuitar(context.Background(), e.app, publishedGuitarTestSeed(t)); err != nil {
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
	for _, p := range []string{"guitar/lessons/gtr_l001", "guitar/profile", "guitar/identity", "guitar/practice"} {
		if s, _, _ := e.request("GET", p, nil, notes); s != 401 {
			t.Fatal("notes session used for guitar", p, s)
		}
	}
	if s, _, _ := e.request("GET", "guitar/courses", nil, nil); s != 200 {
		t.Fatal("course catalog", s)
	}
	s, c, _ := e.request("GET", "guitar/courses/beginner-book", nil, nil)
	if s != 200 || len(c["chapters"].([]any)) != 10 || c["course"].(map[string]any)["lessonCount"] != float64(70) {
		t.Fatal("course hierarchy", s, c)
	}
	chapterTwo := c["chapters"].([]any)[1].(map[string]any)["lessons"].([]any)
	if chapterTwo[0].(map[string]any)["ready"] != true || chapterTwo[4].(map[string]any)["ready"] != false {
		t.Fatal("engraving status was hidden or misrepresented")
	}
	if s, _, _ := e.request("GET", "guitar/lessons/gtr_l001", nil, guitar); s != 200 {
		t.Fatal("lesson", s)
	}
	if s, _, _ := e.request("GET", "guitar/lessons/beginner-01", nil, guitar); s != 404 {
		t.Fatal("archived draft was exposed as the new curriculum", s)
	}
	s, scores, _ := e.request("GET", "guitar/scores", nil, guitar)
	if s != 200 {
		t.Fatal(s)
	}
	found := false
	for _, item := range scores["scores"].([]any) {
		row := item.(map[string]any)
		if row["id"] == "course-little-bee" {
			if _, ok := row["preview"]; ok {
				t.Fatal("list returned unnecessary notation preview")
			}
			found = true
		}
	}
	if !found {
		t.Fatal("course score missing from library")
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
	if s, o, _ := e.request("PUT", "guitar/lessons/gtr_l003/progress", map[string]any{"completed": true, "checks": []string{"已复查"}}, one); s != 200 {
		t.Fatal(s, o)
	}
	if s, _, _ := e.request("POST", "guitar/lessons/gtr_l003/open", nil, one); s != 200 {
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
	if s, _, _ := e.request("PUT", "guitar/lessons/gtr_l003/progress", map[string]any{"completed": true, "userId": "other"}, one); s != 400 {
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
	if s, _, _ := e.request("POST", "guitar/courses/beginner-book/questions", map[string]any{"body": "如何练习？"}, nil); s != 401 {
		t.Fatal("anonymous learning post", s)
	}
	if s, _, _ := e.request("POST", "guitar/courses/intermediate/questions", map[string]any{"body": "如何练习？", "lessonId": "gtr_l003"}, one); s != 400 {
		t.Fatal("cross-course context accepted", s)
	}
	s, q, _ := e.request("POST", "guitar/courses/beginner-book/questions", map[string]any{"body": "如何逐弦调音？", "lessonId": "gtr_l003"}, one)
	if s != 201 {
		t.Fatal(s, q)
	}
	if s, _, _ := e.request("POST", "guitar/questions/"+q["id"].(string)+"/replies", map[string]string{"body": "先确认弦号。"}, one); s != 201 {
		t.Fatal("reply", s)
	}
	s, qs, _ := e.request("GET", "guitar/courses/beginner-book/questions", nil, nil)
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
	for k, v := range map[string]string{"title": "个人原创练习", "artist": "测试作者", "category": "指弹", "difficultyStars": "2.5", "permission": "confirmed", "key": "F♯m", "capo": "0", "meter": "6/8", "bpm": "90", "chords": "F♯m、A，E/B F#m"} {
		form.WriteField(k, v)
	}
	f, _ := form.CreateFormFile("file", "practice.png")
	png.Encode(f, image.NewRGBA(image.Rect(0, 0, 8, 8)))
	for _, name := range []string{"page2.png", "page3.png"} {
		f, _ := form.CreateFormFile("file", name)
		png.Encode(f, image.NewRGBA(image.Rect(0, 0, 16, 8)))
	}
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
	var confirmed bool
	if err = e.db.QueryRow(context.Background(), "SELECT permission_confirmed_at IS NOT NULL FROM guitar_uploads WHERE id=$1", id).Scan(&confirmed); err != nil || !confirmed {
		t.Fatal("upload permission confirmation missing", err)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 404 {
		t.Fatal("unreviewed upload exposed", s)
	}
	if s, list, _ := e.request("GET", "guitar/uploads?page=2&pageSize=1", nil, one); s != 200 || len(list["uploads"].([]any)) != 0 || list["pagination"].(map[string]any)["total"] != float64(1) {
		t.Fatal("upload page metadata is inconsistent", s, list)
	}
	if err = reviewGuitar(context.Background(), e.db, []string{"approve", id}); err != nil {
		t.Fatal(err)
	}
	if err = e.db.QueryRow(context.Background(), "SELECT reviewed_at IS NOT NULL FROM guitar_uploads WHERE id=$1", id).Scan(&confirmed); err != nil || !confirmed {
		t.Fatal("review time missing", err)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, two); s != 404 {
		t.Fatal("another user removed upload", s)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, one); s != 200 {
		t.Fatal("remove record", s)
	}
	if s, public, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 200 || public["artist"] != "测试作者" || public["genre"] != "" || public["description"] != "" || public["difficultyStars"] != 2.5 {
		t.Fatal("public resource or metadata removed with personal record", s, public)
	}
	if s, out, _ := e.request("GET", "guitar/scores/"+score, nil, nil); s != 200 || len(out["pages"].([]any)) != 3 {
		t.Fatal("anonymous multipage metadata", s, out)
	} else if out["key"] != "F#m" || out["bpm"] != float64(90) || out["capo"] != float64(0) ||
		!reflect.DeepEqual(out["meter"], []any{float64(6), float64(8)}) || !reflect.DeepEqual(out["chords"], []any{"F#m", "A", "E/B"}) {
		t.Fatal("review or personal removal lost score attributes", out)
	}
	status, list, _ := e.request("GET", "guitar/scores?q=个人原创练习", nil, nil)
	if status != 200 || len(list["scores"].([]any)) != 1 {
		t.Fatal(status, list)
	}
	card := list["scores"].([]any)[0].(map[string]any)
	if card["capo"] != float64(0) || card["key"] != "F#m" || card["bpm"] != float64(90) {
		t.Fatal("catalog lost score attributes", card)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score+"/pages/2", nil, nil); s != 200 {
		t.Fatal("public page missing after upload removed", s)
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
	res, err = http.Get(e.server.URL + "/api/v1/apps/guitar/scores/" + score + "/download")
	if err != nil {
		t.Fatal(err)
	}
	data, _ = io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || res.Header.Get("Content-Type") != "application/zip" || !strings.HasPrefix(res.Header.Get("Content-Disposition"), "attachment;") {
		t.Fatal("anonymous download failed", res.StatusCode, res.Header)
	}
	archive, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil || len(archive.File) != 3 {
		t.Fatal("download lost score pages", err)
	}
	for i, f := range archive.File {
		if !strings.HasSuffix(f.Name, fmt.Sprintf("-%02d.png", i+1)) || strings.ContainsAny(f.Name, `/\`) {
			t.Fatal("invalid page filename or order", f.Name)
		}
	}
}

func TestGuitarArtistPhotoIsAvailableToRecommendations(t *testing.T) {
	e := guitarTestEnv(t)
	const score = "artist-photo-song"
	guitarHomeScore(t, e, score, "测试图片歌手", "弹唱", "published", 0)
	var id string
	if err := e.db.QueryRow(context.Background(), `SELECT artist_id::text FROM guitar_scores WHERE id=$1`, score).Scan(&id); err != nil {
		t.Fatal(err)
	}
	file := filepath.Join(t.TempDir(), "artist.png")
	var raw bytes.Buffer
	if err := png.Encode(&raw, image.NewRGBA(image.Rect(0, 0, 16, 8))); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(file, raw.Bytes(), 0600); err != nil {
		t.Fatal(err)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"artist-photo", id, file}); err != nil {
		t.Fatal(err)
	}
	status, home, _ := e.request("GET", "guitar/home", nil, nil)
	if status != 200 {
		t.Fatal(status, home)
	}
	var imageURL string
	for _, item := range home["scores"].([]any) {
		if row := item.(map[string]any); row["id"] == score {
			imageURL, _ = row["artistAvatarUrl"].(string)
		}
	}
	if !strings.HasPrefix(imageURL, "/api/v1/apps/guitar/artists/"+id+"/avatar?v=") {
		t.Fatal("recommendation missing artist photo", imageURL)
	}
	res, err := http.Get(e.server.URL + imageURL)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(res.Body)
	if err != nil || res.StatusCode != 200 || res.Header.Get("Content-Type") != "image/png" || !bytes.Equal(data, raw.Bytes()) {
		t.Fatal("could not read uploaded artist photo", err, res.StatusCode)
	}
}
func TestGuitarSeedAndRank(t *testing.T) {
	e := guitarTestEnv(t)
	if err := importGuitar(context.Background(), e.app, guitarSeed); err != nil {
		t.Fatal(err)
	}
	var n int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM guitar_lessons").Scan(&n); err != nil || n != 94 {
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
	if s, _, _ := e.request("PUT", "guitar/lessons/gtr_l001/progress", map[string]any{"completed": true, "checks": []string{}}, cookie); s != 200 {
		t.Fatal(s)
	}
	var seed map[string]any
	if err := json.Unmarshal(publishedGuitarTestSeed(t), &seed); err != nil {
		t.Fatal(err)
	}
	course := seed["courses"].([]any)[0].(map[string]any)
	course["title"] = "修订后的初级教程"
	raw, _ := json.Marshal(seed)
	ctx := context.Background()
	if err := importGuitar(ctx, e.app, raw); err != nil {
		t.Fatal(err)
	}
	_, before, _ := e.request("GET", "guitar/courses/beginner-book", nil, nil)
	if before["course"].(map[string]any)["title"] == course["title"] {
		t.Fatal("default import overwrote existing content")
	}
	if err := importGuitarContent(ctx, e.app, raw, true); err != nil {
		t.Fatal(err)
	}
	_, after, _ := e.request("GET", "guitar/courses/beginner-book", nil, nil)
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

func TestGuitarArchivedCoursePreservesRecordsAndIndependentScores(t *testing.T) {
	e := guitarTestEnv(t)
	cookie := guitarTestAccount(t, e, "archived-course")
	for _, target := range []string{"course/beginner-book", "lesson/gtr_l007", "score/course-little-bee"} {
		if s, _, _ := e.request("PUT", "guitar/favorites/"+target, nil, cookie); s != 200 {
			t.Fatal(s)
		}
	}
	if s, _, _ := e.request("PUT", "guitar/lessons/gtr_l007/progress", map[string]any{"completed": true, "checks": []string{}}, cookie); s != 200 {
		t.Fatal(s)
	}
	ctx := context.Background()
	// The shipped seed keeps the shelved course archived by default.
	if err := importGuitarContent(ctx, e.app, guitarSeed, true); err != nil {
		t.Fatal(err)
	}
	for _, route := range []string{"courses/beginner-book", "lessons/gtr_l007"} {
		if s, _, _ := e.request("GET", "guitar/"+route, nil, cookie); s != 404 {
			t.Fatal("archived learning content still available", route, s)
		}
	}
	_, catalog, _ := e.request("GET", "guitar/courses", nil, nil)
	for _, row := range catalog["courses"].([]any) {
		if row.(map[string]any)["id"] == "beginner-book" {
			t.Fatal("archived course listed")
		}
	}
	_, favorites, _ := e.request("GET", "guitar/favorites", nil, cookie)
	items := favorites["favorites"].([]any)
	if len(items) != 1 || items[0].(map[string]any)["kind"] != "score" {
		t.Fatal("unavailable learning links remain", favorites)
	}
	_, profile, _ := e.request("GET", "guitar/profile", nil, cookie)
	if len(profile["progress"].([]any)) != 0 {
		t.Fatal("archived course offered as recent learning")
	}
	s, score, _ := e.request("GET", "guitar/scores/course-little-bee", nil, cookie)
	if s != 200 || score["courseAvailable"] != false {
		t.Fatal("independent score or availability incorrect", s, score)
	}
	// Restoring the same course must restore the existing favorites and progress.
	if err := importGuitarContent(ctx, e.app, publishedGuitarTestSeed(t), true); err != nil {
		t.Fatal(err)
	}
	_, favorites, _ = e.request("GET", "guitar/favorites", nil, cookie)
	_, profile, _ = e.request("GET", "guitar/profile", nil, cookie)
	if len(favorites["favorites"].([]any)) != 3 || len(profile["progress"].([]any)) != 1 || profile["progress"].([]any)[0].(map[string]any)["completed"] != true {
		t.Fatal("archiving lost personal records")
	}
}
