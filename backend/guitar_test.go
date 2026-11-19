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

	"github.com/jackc/pgx/v5"
)

func seedLegacyGuitarCourseFixtures(t *testing.T, e *testEnv) {
	t.Helper()
	if _, err := e.db.Exec(context.Background(), `
INSERT INTO guitar_courses(id,title,summary,level,status)
VALUES('beginner-book','旧课程','旧课程摘要','入门','archived');
INSERT INTO guitar_chapters(id,course_id,title,sort_order)
VALUES('archived-chapter','beginner-book','旧章节',0);
INSERT INTO guitar_lessons(id,chapter_id,title,sort_order,content)
VALUES('gtr_l003','archived-chapter','第三课',0,'{"original":"三"}'),
      ('gtr_l007','archived-chapter','第七课',1,'{"original":"七"}');`); err != nil {
		t.Fatal(err)
	}
}

func archiveGuitarCourseFixtures(t *testing.T, e *testEnv) string {
	t.Helper()
	ctx := context.Background()
	var runtimeSchema string
	if err := e.db.QueryRow(ctx, "SELECT current_schema()").Scan(&runtimeSchema); err != nil {
		t.Fatal(err)
	}
	archive := pgx.Identifier{runtimeSchema + "_archive"}.Sanitize()
	t.Cleanup(func() {
		if _, err := e.db.Exec(ctx, "DROP SCHEMA IF EXISTS "+archive+" CASCADE"); err != nil {
			t.Error("course archive cleanup:", err)
		}
	})
	migration := migrationFixture("026_guitar_archive_courses")
	if _, err := e.db.Exec(ctx, strings.ReplaceAll(migration, "kimcloud", runtimeSchema)); err != nil {
		t.Fatal("course archive migration:", err)
	}
	return archive
}

func TestGuitarImagePageWithUnknownDimensions(t *testing.T) {
	page := guitarScoreImagePage(1, "/example", []byte("not an image"))
	if page["url"] != "/example" || page["page"] != 1 || page["width"] != nil || page["height"] != nil {
		t.Fatal("unknown dimensions should not make a score unreadable", page)
	}
}
func guitarTestEnv(t *testing.T) *testEnv {
	t.Helper()
	e := guitarTestEnvBeforeCourseArchive(t)
	archiveGuitarCourseFixtures(t, e)
	return e
}

func guitarTestEnvBeforeCourseArchive(t *testing.T) *testEnv {
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
	for _, schema := range []string{
		guitarScoreAttributesSchema,
		guitarArtistPhotosSchema,
		guitarSignatureSchema,
		accountPointsSchema,
		guitarPrivateUploadsSchema,
		guitarUploadLifecycleSchema,
	} {
		if _, err := e.db.Exec(context.Background(), schema); err != nil {
			t.Fatal(err)
		}
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
	for _, p := range []string{"guitar/profile", "guitar/identity", "guitar/practice"} {
		if s, _, _ := e.request("GET", p, nil, notes); s != 401 {
			t.Fatal("notes session used for guitar", p, s)
		}
	}
	for _, route := range []string{"guitar/courses", "guitar/courses/beginner-book", "guitar/lessons/gtr_l001", "guitar/courses/beginner-book/questions"} {
		if s, _, _ := e.request("GET", route, nil, guitar); s != 404 {
			t.Fatal("retired course route is available", route, s)
		}
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
	if s, _, _ := e.request("PUT", "guitar/favorites/course/intermediate", nil, guitar); s != 400 {
		t.Fatal("retired course favorite was accepted", s)
	}
}
func TestGuitarPrivateProgressFavoritesAndVisits(t *testing.T) {
	e := guitarTestEnvBeforeCourseArchive(t)
	seedLegacyGuitarCourseFixtures(t, e)
	one, two := guitarTestAccount(t, e, "guitar-one"), guitarTestAccount(t, e, "guitar-two")
	_, account, _ := e.request("GET", "guitar/auth/me", nil, one)
	userID := account["user"].(map[string]any)["id"].(string)
	if _, err := e.db.Exec(context.Background(), `INSERT INTO guitar_progress(app_id,user_id,lesson_id,completed,checks) VALUES('guitar',$1,'gtr_l003',true,'["已复查"]')`, userID); err != nil {
		t.Fatal(err)
	}
	archive := archiveGuitarCourseFixtures(t, e)
	if s, _, _ := e.request("PUT", "guitar/favorites/score/single-string", nil, one); s != 200 {
		t.Fatal(s)
	}
	for i := 0; i < 3; i++ {
		s, p, _ := e.request("POST", "guitar/visits", nil, one)
		if s != 200 || p["totalDays"] != float64(1) {
			t.Fatal("daily open double counted", s, p)
		}
		if len(p["progress"].([]any)) != 0 {
			t.Fatal("retired lesson progress was exposed")
		}
	}
	var completed bool
	if err := e.db.QueryRow(context.Background(), `SELECT completed FROM `+archive+`.guitar_progress WHERE app_id='guitar' AND user_id=$1 AND lesson_id='gtr_l003'`, userID).Scan(&completed); err != nil || !completed {
		t.Fatal("visits changed archived progress", completed, err)
	}
	if s, p, _ := e.request("GET", "guitar/profile", nil, two); s != 200 || len(p["progress"].([]any)) != 0 || p["totalDays"] != float64(0) {
		t.Fatal("another user's progress leaked", s, p)
	}
	if s, f, _ := e.request("GET", "guitar/favorites", nil, two); s != 200 || len(f["favorites"].([]any)) != 0 {
		t.Fatal("another user's favorites leaked", s, f)
	}
	if s, _, _ := e.request("PUT", "guitar/lessons/gtr_l003/progress", map[string]any{"completed": false}, one); s != 404 {
		t.Fatal("retired progress endpoint accepted writes", s)
	}
}
func TestGuitarAnonymousFeedback(t *testing.T) {
	e := guitarTestEnv(t)
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
	longFeedback := strings.Repeat("谱", 2000)
	if s, _, _ := e.request("POST", "guitar/messages", map[string]string{"type": "缺少曲谱", "body": longFeedback}, nil); s != 201 {
		t.Fatal("typed feedback at length limit", s)
	}
	_, m, _ = e.request("GET", "guitar/messages", nil, nil)
	feedback := m["messages"].([]any)[0].(map[string]any)
	if feedback["nickname"] != "匿名琴友" || feedback["body"] != "[缺少曲谱] "+longFeedback {
		t.Fatal("typed feedback lost anonymity, type, or body")
	}
}
func TestGuitarPublishedUploadSurvivesPersonalRemoval(t *testing.T) {
	e := guitarTestEnv(t)
	one, two := guitarTestAccount(t, e, "uploader"), guitarTestAccount(t, e, "reader")
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	for k, v := range map[string]string{"title": "个人原创练习", "artist": "测试作者", "genre": "legacy-unrecognized", "category": "指弹", "difficultyStars": "2.5", "permission": "confirmed", "key": "F♯m", "capo": "0", "meter": "6/8", "bpm": "90", "chords": "F♯m、A，E/B F#m"} {
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
	var legacyGenre string
	if err = e.db.QueryRow(context.Background(), `SELECT genre FROM guitar_uploads WHERE id=$1`, id).Scan(&legacyGenre); err != nil || legacyGenre != "" {
		t.Fatal("retired genre input should be ignored", legacyGenre, err)
	}
	// Uploads submitted before the genre field was retired keep their metadata
	// when an administrator approves them after this change.
	if _, err = e.db.Exec(context.Background(), `UPDATE guitar_uploads SET genre='民谣' WHERE id=$1`, id); err != nil {
		t.Fatal(err)
	}
	if err = reviewGuitar(context.Background(), e.db, []string{"approve", id}); err != nil {
		t.Fatal(err)
	}
	if err = e.db.QueryRow(context.Background(), `SELECT genre FROM guitar_scores WHERE id=$1`, score).Scan(&legacyGenre); err != nil || legacyGenre != "民谣" {
		t.Fatal("approving a legacy upload lost its genre data", legacyGenre, err)
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
	if s, public, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 200 || public["artist"] != "测试作者" || public["description"] != "" || public["difficultyStars"] != 2.5 {
		t.Fatal("public resource or metadata removed with personal record", s, public)
	}
	if s, out, _ := e.request("GET", "guitar/scores/"+score, nil, two); s != 200 || len(out["pages"].([]any)) != 3 {
		t.Fatal("signed-in multipage metadata", s, out)
	} else if out["key"] != "F#m" || out["bpm"] != float64(90) || out["capo"] != float64(0) ||
		!reflect.DeepEqual(out["meter"], []any{float64(6), float64(8)}) || !reflect.DeepEqual(out["chords"], []any{"F#m", "A", "E/B"}) {
		t.Fatal("review or personal removal lost score attributes", out)
	} else {
		pages := out["pages"].([]any)
		first, second := pages[0].(map[string]any), pages[1].(map[string]any)
		if first["width"] != float64(8) || first["height"] != float64(8) || second["width"] != float64(16) || second["height"] != float64(8) {
			t.Fatal("page dimensions are missing or incorrect", pages)
		}
	}
	status, list, _ := e.request("GET", "guitar/scores?q=个人原创练习", nil, nil)
	if status != 200 || len(list["scores"].([]any)) != 1 {
		t.Fatal(status, list)
	}
	card := list["scores"].([]any)[0].(map[string]any)
	if card["capo"] != float64(0) || card["key"] != "F#m" || card["bpm"] != float64(90) {
		t.Fatal("catalog lost score attributes", card)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score+"/pages/2", nil, two); s != 200 {
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
	if status, _, _ := e.request("POST", "guitar/account/points/check-in", nil, two); status != 200 {
		t.Fatal("check-in failed", status)
	}
	req, _ = http.NewRequest("POST", e.server.URL+"/api/v1/apps/guitar/scores/"+score+"/download", nil)
	req.Header.Set("Origin", "https://notes.test")
	req.AddCookie(two)
	res, err = http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	data, _ = io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || res.Header.Get("Content-Type") != "application/zip" || !strings.HasPrefix(res.Header.Get("Content-Disposition"), "attachment;") {
		t.Fatal("signed-in download failed", res.StatusCode, res.Header)
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
	var seed struct {
		Scores []guitarSeedScore `json:"scores"`
	}
	if err := json.Unmarshal(guitarSeed, &seed); err != nil {
		t.Fatal(err)
	}
	scoresOnly, err := json.Marshal(seed)
	if err != nil {
		t.Fatal(err)
	}
	if err := importGuitar(context.Background(), e.app, scoresOnly); err != nil {
		t.Fatal("score-only import after archiving courses:", err)
	}
	if err := importGuitar(context.Background(), e.app, guitarSeed); err != nil {
		t.Fatal("mixed legacy import after archiving courses:", err)
	}
	var n int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM guitar_scores").Scan(&n); err != nil || n != len(seed.Scores) {
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

func TestGuitarArchivedCourseDataIsPreserved(t *testing.T) {
	e := guitarTestEnvBeforeCourseArchive(t)
	seedLegacyGuitarCourseFixtures(t, e)
	cookie := guitarTestAccount(t, e, "archived-course")
	_, account, _ := e.request("GET", "guitar/auth/me", nil, cookie)
	userID := account["user"].(map[string]any)["id"].(string)
	ctx := context.Background()
	for _, target := range []struct{ kind, id string }{{"course", "beginner-book"}, {"lesson", "gtr_l007"}, {"score", "course-little-bee"}} {
		if _, err := e.db.Exec(ctx, `INSERT INTO guitar_favorites(app_id,user_id,kind,target_id) VALUES('guitar',$1,$2,$3)`, userID, target.kind, target.id); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_progress(app_id,user_id,lesson_id,completed,checks) VALUES('guitar',$1,'gtr_l007',true,'["已复查"]')`, userID); err != nil {
		t.Fatal(err)
	}
	questionID, replyID := newID(), newID()
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_questions(id,user_id,course_id,lesson_id,body) VALUES($1,$2,'beginner-book','gtr_l007','原始问题')`, questionID, userID); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_replies(id,user_id,question_id,body) VALUES($1,$2,$3,'原始回复')`, replyID, userID, questionID); err != nil {
		t.Fatal(err)
	}
	tables := []string{"guitar_courses", "guitar_chapters", "guitar_lessons", "guitar_progress", "guitar_questions", "guitar_replies"}
	before := make(map[string]int)
	for _, table := range tables {
		var count int
		if err := e.db.QueryRow(ctx, "SELECT count(*) FROM "+table).Scan(&count); err != nil || count == 0 {
			t.Fatalf("historical fixture %s has %d rows: %v", table, count, err)
		}
		before[table] = count
	}
	var lessonBefore string
	if err := e.db.QueryRow(ctx, `SELECT content::text FROM guitar_lessons WHERE id='gtr_l007'`).Scan(&lessonBefore); err != nil {
		t.Fatal(err)
	}
	archive := archiveGuitarCourseFixtures(t, e)
	for _, table := range tables {
		var stillInSearchPath bool
		if err := e.db.QueryRow(ctx, "SELECT to_regclass($1) IS NOT NULL", table).Scan(&stillInSearchPath); err != nil || stillInSearchPath {
			t.Fatalf("archived table %s is still active: %v", table, err)
		}
		var count int
		if err := e.db.QueryRow(ctx, "SELECT count(*) FROM "+archive+"."+table).Scan(&count); err != nil || count != before[table] {
			t.Fatalf("archived table %s has %d rows; want %d: %v", table, count, before[table], err)
		}
	}
	for _, route := range []string{"courses", "courses/beginner-book", "lessons/gtr_l007", "courses/beginner-book/questions"} {
		if status, _, _ := e.request("GET", "guitar/"+route, nil, cookie); status != 404 {
			t.Fatal("retired route exposed", route, status)
		}
	}
	if status, _, _ := e.request("PUT", "guitar/lessons/gtr_l007/progress", map[string]any{"completed": false}, cookie); status != 404 {
		t.Fatal("retired progress still writable", status)
	}
	if status, _, _ := e.request("DELETE", "guitar/favorites/lesson/gtr_l007", nil, cookie); status != 400 {
		t.Fatal("archived favorite can be deleted through retired API", status)
	}
	_, favorites, _ := e.request("GET", "guitar/favorites", nil, cookie)
	items := favorites["favorites"].([]any)
	if len(items) != 1 || items[0].(map[string]any)["id"] != "course-little-bee" {
		t.Fatal("independent score favorite unavailable", favorites)
	}
	if status, _, _ := e.request("GET", "guitar/scores/course-little-bee", nil, cookie); status != 200 {
		t.Fatal("independent score unavailable", status)
	}
	var archivedFavorites int
	var lessonAfter, checks string
	var completed bool
	if err := e.db.QueryRow(ctx, `SELECT (SELECT content::text FROM `+archive+`.guitar_lessons WHERE id='gtr_l007'),
(SELECT count(*) FROM guitar_favorites WHERE user_id=$1 AND kind IN ('course','lesson')),completed,checks::text
FROM `+archive+`.guitar_progress WHERE user_id=$1 AND lesson_id='gtr_l007'`, userID).Scan(&lessonAfter, &archivedFavorites, &completed, &checks); err != nil {
		t.Fatal(err)
	}
	if lessonBefore != lessonAfter || archivedFavorites != 2 || !completed || !strings.Contains(checks, "已复查") {
		t.Fatal("archived records were changed", lessonBefore, lessonAfter, archivedFavorites, completed, checks)
	}
}
