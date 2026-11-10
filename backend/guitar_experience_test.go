package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"image"
	"image/png"
	"strings"
	"testing"
)

func TestGuitarVerifiedRegistrationKeepsMember(t *testing.T) {
	e := guitarTestEnv(t)
	e.app.config.RequireVerification = true
	e.app.config.SMTPHost = "smtp.test"
	e.app.config.MailFrom = "notes@example.com"
	var mail string
	e.app.sendMail = func(_ context.Context, _, _, body string) error { mail = body; return nil }
	s, out, _ := e.request("POST", "guitar/auth/register", credentials{Email: "verified-guitar@example.com", Password: "Test-password-123", Nickname: "晚风", Avatar: "leaf"}, nil)
	if s != 202 {
		t.Fatal(s, out)
	}
	code := strings.Split(strings.Split(mail, "：")[1], "\r")[0]
	s, _, response := e.request("POST", "guitar/auth/verify", map[string]string{"challengeId": out["challengeId"].(string), "code": code}, nil)
	if s != 200 {
		t.Fatal(s)
	}
	_, out, _ = e.request("GET", "guitar/session", nil, response.Cookies()[0])
	user := out["user"].(map[string]any)
	if user["nickname"] != "晚风" || user["avatar"] != "leaf" {
		t.Fatal("verified registration lost profile", user)
	}
}

func TestGuitarPublicReadingAndMemberPrivacy(t *testing.T) {
	e := guitarTestEnv(t)
	for _, path := range []string{"guitar/scores", "guitar/scores/course-little-bee"} {
		if s, _, _ := e.request("GET", path, nil, nil); s != 200 {
			t.Fatal(path, s)
		}
	}
	for _, path := range []string{"guitar/identity", "guitar/practice", "guitar/favorites", "guitar/uploads", "guitar/profile"} {
		if s, _, _ := e.request("GET", path, nil, nil); s != 401 {
			t.Fatal("personal data became public", path, s)
		}
	}
	s, _, res := e.request("POST", "guitar/auth/register", credentials{Email: "identity-guitar@example.com", Password: "Test-password-123", Nickname: "听风的人", Avatar: "vinyl"}, nil)
	if s != 200 {
		t.Fatal(s)
	}
	var cookie = res.Cookies()[0]
	s, out, _ := e.request("GET", "guitar/session", nil, cookie)
	if s != 200 {
		t.Fatal(s, out)
	}
	u := out["user"].(map[string]any)
	if u["nickname"] != "听风的人" || u["avatar"] != "vinyl" {
		t.Fatal(u)
	}
	s, _, _ = e.request("POST", "guitar/messages", map[string]string{"nickname": "冒充站长", "body": "想找一首指弹曲谱"}, nil)
	if s != 201 {
		t.Fatal(s)
	}
	_, out, _ = e.request("GET", "guitar/messages", nil, nil)
	if out["messages"].([]any)[0].(map[string]any)["nickname"] != "匿名琴友" {
		t.Fatal("anonymous name spoofed", out)
	}
	s, _, _ = e.request("POST", "guitar/messages", map[string]string{"body": "希望增加按歌名快捷查找"}, cookie)
	if s != 201 {
		t.Fatal(s)
	}
	_, out, _ = e.request("GET", "guitar/messages", nil, nil)
	m := out["messages"].([]any)[0].(map[string]any)
	if m["nickname"] != "听风的人" || m["avatar"] != "vinyl" {
		t.Fatal(m)
	}
	if _, exists := m["email"]; exists {
		t.Fatal("email leaked into community")
	}
	s, _, _ = e.request("PUT", "guitar/identity", map[string]any{"nickname": "新昵称", "avatar": "cat", "resetAvatar": true}, cookie)
	if s != 200 {
		t.Fatal(s)
	}
	var img bytes.Buffer
	png.Encode(&img, image.NewRGBA(image.Rect(0, 0, 20, 20)))
	s, out, _ = e.request("PUT", "guitar/identity/avatar", map[string]string{"dataUrl": "data:image/png;base64," + base64.StdEncoding.EncodeToString(img.Bytes())}, cookie)
	if s != 200 || out["avatarUrl"] == "" {
		t.Fatal(s, out)
	}
	s, _, _ = e.request("PUT", "guitar/identity/avatar", map[string]string{"dataUrl": "data:image/svg+xml;base64," + base64.StdEncoding.EncodeToString([]byte("<svg/>"))}, cookie)
	if s != 400 {
		t.Fatal("non-raster avatar accepted", s)
	}
	s, out, _ = e.request("POST", "guitar/practice", map[string]string{"scoreId": "course-little-bee"}, cookie)
	if s != 201 {
		t.Fatal(s, out)
	}
	id := out["id"].(string)
	e.db.Exec(context.Background(), `UPDATE guitar_practice_sessions SET started_at=now()-interval '90 seconds' WHERE id=$1`, id)
	s, out, _ = e.request("PUT", "guitar/practice/"+id, nil, cookie)
	if s != 200 || out["seconds"].(float64) < 89 {
		t.Fatal(s, out)
	}
	other := guitarTestAccount(t, e, "other-practice")
	if s, _, _ := e.request("PUT", "guitar/practice/"+id, nil, other); s != 404 {
		t.Fatal("practice crossed accounts", s)
	}
	_, first, _ := e.request("GET", "guitar/practice", nil, cookie)
	e.request("PUT", "guitar/practice/"+id, nil, cookie)
	_, second, _ := e.request("GET", "guitar/practice", nil, cookie)
	if first["totalSeconds"] != second["totalSeconds"] || first["sessions"] != float64(1) {
		t.Fatal("stop was not idempotent", first, second)
	}
}
