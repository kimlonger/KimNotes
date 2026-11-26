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
	"strconv"
	"testing"
)

func TestGuitarAdminDeviceBoundaryAndReview(t *testing.T) {
	e := guitarTestEnv(t)
	adminWeb := guitarTestAccount(t, e, "guitar-admin")
	_ = guitarTestAccount(t, e, "guitar-submitter")
	var adminID string
	if err := e.db.QueryRow(context.Background(), `SELECT id::text FROM users WHERE email=$1`,
		"guitar-admin@example.com").Scan(&adminID); err != nil {
		t.Fatal(err)
	}
	e.app.config.GuitarAdminUserID = adminID
	admin := guitarAndroidLogin(t, e, "guitar-admin", "3")["token"].(string)
	owner := guitarAndroidLogin(t, e, "guitar-submitter", "3")["token"].(string)

	call := func(method, path string, data any, token string, expected int) map[string]any {
		t.Helper()
		status, out, _ := e.sessionRequest(method, "guitar/"+path, data, nil, token, guitarAndroidHeaders)
		if status != expected {
			t.Fatalf("%s %s: got %d want %d: %v", method, path, status, expected, out)
		}
		return out
	}
	if result := call("GET", "workbench", nil, admin, 200); result["canManageGuitar"] != true {
		t.Fatal("the designated Android account cannot see moderation")
	}
	if result := call("GET", "workbench", nil, owner, 200); result["canManageGuitar"] != nil {
		t.Fatal("the normal Android account sees moderation")
	}
	call("GET", "admin/uploads", nil, "", 401)
	call("GET", "admin/uploads", nil, owner, 403)
	call("GET", "admin/artists", nil, owner, 403)
	call("GET", "admin/messages?kind=suggestions", nil, owner, 403)
	if status, _, _ := e.request("GET", "guitar/admin/uploads", nil, adminWeb); status != 403 {
		t.Fatalf("web session bypassed Android scope: %d", status)
	}

	id, score := largeAndroidUpload(t, e, owner)
	list := call("GET", "admin/uploads", nil, admin, 200)["items"].([]any)
	if len(list) != 1 || list[0].(map[string]any)["id"] != id || list[0].(map[string]any)["pages"] != float64(3) {
		t.Fatal("submitted pages missing from the moderation queue", list)
	}
	file := "admin/uploads/" + id + "/files/3"
	call("GET", file, nil, owner, 403)
	status, _, response := e.sessionRequest("GET", "guitar/"+file, nil, nil, admin, guitarAndroidHeaders)
	if status != 200 || response.Header.Get("Content-Type") != "image/png" || response.Header.Get("Cache-Control") != "private, no-store" {
		t.Fatalf("admin file access: %d %v", status, response.Header)
	}
	review := "admin/uploads/" + id + "/review"
	approve := map[string]string{"decision": "approve"}
	call("POST", review, approve, owner, 403)
	call("GET", "scores/"+score, nil, admin, 404)
	call("POST", review, approve, admin, 200)
	call("POST", review, approve, admin, 409)
	call("GET", "scores/"+score, nil, owner, 200)
	if result := call("GET", "admin/uploads?status=approved", nil, admin, 200); len(result["items"].([]any)) != 1 {
		t.Fatal("approved upload is missing from its history")
	}

	id, score = privateUploadFixture(t, e, adminWeb, false)
	call("POST", "admin/uploads/"+id+"/review", map[string]string{"decision": "reject", "note": "请补充最后一页。"}, admin, 200)
	call("GET", "scores/"+score, nil, owner, 404)
	if entries := call("GET", "admin/uploads?status=rejected", nil, admin, 200)["items"].([]any); len(entries) != 1 ||
		entries[0].(map[string]any)["reviewNote"] != "请补充最后一页。" {
		t.Fatal("reject reason missing", entries)
	}

	suggestion := newID()
	question := newID()
	e.sql(`INSERT INTO guitar_messages(id,nickname,body) VALUES($1,'测试琴友','建议测试')`, suggestion)
	e.sql(`INSERT INTO guitar_messages(id,nickname,body,question_title) VALUES($1,'测试琴友','问题内容','问题标题')`, question)
	change := map[string]string{"status": "accepted", "reply": "已经安排。"}
	call("PATCH", "admin/messages/"+suggestion, change, owner, 403)
	call("PATCH", "admin/messages/"+suggestion, change, admin, 200)
	posts := call("GET", "admin/messages?kind=suggestions", nil, admin, 200)["items"].([]any)
	if len(posts) != 1 || posts[0].(map[string]any)["reply"] != "已经安排。" {
		t.Fatal("suggestion reply missing", posts)
	}
	call("PATCH", "admin/messages/"+question, map[string]string{"status": "hidden"}, admin, 200)
	call("GET", "community/questions/"+question, nil, owner, 404)
	call("PATCH", "admin/messages/"+question, map[string]string{"status": "open"}, admin, 200)
	call("GET", "community/questions/"+question, nil, owner, 200)

	artist := newID()
	e.sql(`INSERT INTO guitar_artists(id,name,name_key) VALUES($1,'测试歌手','moderation-artist')`, artist)
	artists := call("GET", "admin/artists?q=%E6%B5%8B%E8%AF%95", nil, admin, 200)["items"].([]any)
	if len(artists) != 1 || artists[0].(map[string]any)["id"] != artist {
		t.Fatal("artist search failed", artists)
	}
	var photo bytes.Buffer
	if err := png.Encode(&photo, image.NewRGBA(image.Rect(0, 0, 4, 4))); err != nil {
		t.Fatal(err)
	}
	photoPath := "/api/v1/apps/guitar/admin/artists/" + artist + "/avatar"
	for _, test := range []struct {
		token string
		want  int
	}{{owner, 403}, {admin, 200}} {
		res := guitarRawDeviceRequest(t, e, "PUT", photoPath, "image/png", photo.Bytes(), test.token)
		res.Body.Close()
		if res.StatusCode != test.want {
			t.Fatalf("set artist photo: %d want %d", res.StatusCode, test.want)
		}
	}
	res, err := http.Get(e.server.URL + "/api/v1/apps/guitar/artists/" + artist + "/avatar")
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(res.Body)
	if err != nil || res.StatusCode != 200 || !bytes.Equal(data, photo.Bytes()) {
		t.Fatal("public artist image did not update")
	}
}

func largeAndroidUpload(t *testing.T, e *testEnv, token string) (string, string) {
	t.Helper()
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	for name, value := range map[string]string{"title": "合成大图上传测试", "category": "弹唱", "difficultyStars": "2", "permission": "confirmed"} {
		if err := form.WriteField(name, value); err != nil {
			t.Fatal(err)
		}
	}
	bitmap := image.NewNRGBA(image.Rect(0, 0, 650, 650))
	pngEncoder := png.Encoder{CompressionLevel: png.NoCompression}
	for page := 1; page <= 3; page++ {
		part, err := form.CreateFormFile("file", "page-"+strconv.Itoa(page)+".png")
		if err != nil {
			t.Fatal(err)
		}
		if err := pngEncoder.Encode(part, bitmap); err != nil {
			t.Fatal(err)
		}
	}
	if err := form.Close(); err != nil {
		t.Fatal(err)
	}
	if body.Len() < 4<<20 || body.Len() >= 10<<20 {
		t.Fatal("fixture size", body.Len())
	}
	response := guitarRawDeviceRequest(t, e, "POST", "/api/v1/apps/guitar/uploads", form.FormDataContentType(), body.Bytes(), token)
	defer response.Body.Close()
	var result map[string]any
	if err := json.NewDecoder(response.Body).Decode(&result); err != nil || response.StatusCode != 201 {
		t.Fatal("large authenticated upload failed", response.StatusCode, result, err)
	}
	t.Log("accepted synthetic Android upload bytes:", body.Len())
	return result["id"].(string), result["previewScoreId"].(string)
}

func guitarRawDeviceRequest(t *testing.T, e *testEnv, method, path, contentType string, body []byte, token string) *http.Response {
	t.Helper()
	request, err := http.NewRequest(method, e.server.URL+path, bytes.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Content-Type", contentType)
	request.Header.Set("Origin", "https://notes.test")
	request.Header.Set("Authorization", "Bearer "+token)
	for name, value := range guitarAndroidHeaders {
		request.Header.Set(name, value)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	return response
}
