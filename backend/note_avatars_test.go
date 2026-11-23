package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"image"
	"image/color"
	"image/png"
	"io"
	"net/http"
	"strings"
	"testing"
)

const noteAvatarPath = "/api/v1/apps/kimnotes/profile/avatar?v="

func testNoteAvatar(t *testing.T, tone color.NRGBA) string {
	t.Helper()
	photo := image.NewNRGBA(image.Rect(0, 0, 160, 80))
	for y := range 80 {
		for x := range 160 {
			if x >= 40 && x < 120 {
				photo.SetNRGBA(x, y, tone)
			}
		}
	}
	var out bytes.Buffer
	if err := png.Encode(&out, photo); err != nil {
		t.Fatal(err)
	}
	return base64.StdEncoding.EncodeToString(out.Bytes())
}

func TestNoteAvatarNormalization(t *testing.T) {
	tone := color.NRGBA{R: 104, G: 170, B: 210, A: 255}
	raw := testNoteAvatar(t, tone)
	file, err := normalizeNoteAvatar(raw)
	if err != nil {
		t.Fatal(err)
	}
	bitmap, err := png.Decode(bytes.NewReader(file))
	if err != nil || bitmap.Bounds().Size() != image.Pt(noteAvatarSide, noteAvatarSide) {
		t.Fatalf("invalid normalized avatar: %v", err)
	}
	if got := color.NRGBAModel.Convert(bitmap.At(0, 0)); got != tone {
		t.Fatalf("center crop result: %v", got)
	}
	var wide bytes.Buffer
	if err := png.Encode(&wide, image.NewNRGBA(image.Rect(0, 0, noteAvatarInputSide+1, 2))); err != nil {
		t.Fatal(err)
	}
	for name, input := range map[string]string{
		"SVG":       base64.StdEncoding.EncodeToString([]byte(`<svg xmlns="http://www.w3.org/2000/svg"></svg>`)),
		"base64":    "this is not valid base64",
		"truncated": raw[:len(raw)/8*4],
		"bytes":     base64.StdEncoding.EncodeToString(make([]byte, noteAvatarLimit+1)),
		"pixels":    base64.StdEncoding.EncodeToString(wide.Bytes()),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := normalizeNoteAvatar(input); err == nil {
				t.Fatal("invalid image was accepted")
			}
		})
	}
}

func TestNoteAvatarOwnershipAndReplacement(t *testing.T) {
	e := newTestEnv(t)
	first := testNoteAvatar(t, color.NRGBA{R: 35, A: 255})
	second := testNoteAvatar(t, color.NRGBA{G: 200, A: 255})
	in := credentials{
		Email: "custom-note-avatar@example.com", Password: "Test-password-123", Nickname: "小羊",
		NoteProfileVersion: 1, NoteAvatarID: "custom",
	}
	if status, _, _ := e.request(http.MethodPost, "kimnotes/auth/register", in, nil); status != 400 {
		t.Fatalf("register accepted custom avatar without image: %d", status)
	}
	in.NoteAvatarImage = first
	status, out, res := e.request(http.MethodPost, "kimnotes/auth/register", in, nil)
	if status != 200 {
		t.Fatalf("custom avatar registration %d: %v", status, out)
	}
	firstURL := assertNoteAvatarURL(t, out, "小羊")
	cookie := res.Cookies()[0]

	code, headers, imageBytes := requestNoteAvatar(t, e, firstURL, cookie)
	if code != 200 || headers.Get("Content-Type") != "image/png" || headers.Get("Cache-Control") != "private, no-store" || headers.Get("X-Content-Type-Options") != "nosniff" {
		t.Fatalf("unexpected avatar response %d: %v", code, headers)
	}
	if cfg, err := png.DecodeConfig(bytes.NewReader(imageBytes)); err != nil || cfg.Width != noteAvatarSide || cfg.Height != noteAvatarSide {
		t.Fatalf("stored avatar not normalized: %v", err)
	}
	if code, _, _ := requestNoteAvatar(t, e, firstURL, e.account("different-note-avatar")); code != 404 {
		t.Fatalf("another account could access the avatar: %d", code)
	}
	if code, _, _ := requestNoteAvatar(t, e, firstURL, nil); code != 401 {
		t.Fatalf("anonymous avatar access: %d", code)
	}
	status, out, _ = e.request(http.MethodPost, "main/auth/login", in, nil)
	if status != 200 || out["user"].(map[string]any)["avatarUrl"] != nil {
		t.Fatalf("Notes avatar URL leaked to main: %d", status)
	}
	status, out, _ = e.request(http.MethodPut, "kimnotes/profile", noteProfile{Nickname: "新昵称", AvatarID: "custom"}, cookie)
	if status != 200 || assertNoteAvatarURL(t, out, "新昵称") != firstURL {
		t.Fatalf("renaming changed or cleared the avatar: %d", status)
	}
	status, out, _ = e.request(http.MethodPut, "kimnotes/profile", map[string]any{
		"nickname": "新头像", "avatarId": "custom", "avatarImage": second,
	}, cookie)
	if status != 200 {
		t.Fatalf("valid avatar replacement %d: %v", status, out)
	}
	secondURL := assertNoteAvatarURL(t, out, "新头像")
	if secondURL == firstURL {
		t.Fatal("replacing the image did not update the URL")
	}
	if code, _, _ := requestNoteAvatar(t, e, firstURL, cookie); code != 404 {
		t.Fatalf("old avatar version remains accessible: %d", code)
	}
	status, out, _ = e.request(http.MethodPut, "kimnotes/profile", noteProfile{Nickname: "使用预设", AvatarID: "mint"}, cookie)
	if status != 200 || assertNoteProfile(t, out, "使用预设", "mint")["avatarUrl"] != nil {
		t.Fatalf("preset choice did not clear the custom avatar: %d", status)
	}
	if code, _, _ := requestNoteAvatar(t, e, secondURL, cookie); code != 404 {
		t.Fatalf("cleared avatar remains accessible: %d", code)
	}
	if status, _, _ := e.request(http.MethodPut, "kimnotes/profile", noteProfile{Nickname: "未保存", AvatarID: "custom"}, cookie); status != 409 {
		t.Fatalf("missing retained avatar update returned %d", status)
	}
	status, out, _ = e.request(http.MethodGet, "kimnotes/auth/me", nil, cookie)
	if status != 200 || assertNoteProfile(t, out, "使用预设", "mint")["avatarUrl"] != nil {
		t.Fatalf("failed retain changed the profile: %d", status)
	}
}

func TestNoteAvatarSurvivesEmailVerification(t *testing.T) {
	e := newTestEnv(t)
	e.app.config.RequireVerification = true
	var sent string
	e.app.sendMail = func(_ context.Context, _, _, body string) error { sent = body; return nil }
	status, pending, _ := e.request(http.MethodPost, "kimnotes/auth/register", credentials{
		Email: "verified-note-avatar@example.com", Password: "Test-password-123", Nickname: "头像小羊",
		NoteProfileVersion: 1, NoteAvatarID: "custom", NoteAvatarImage: testNoteAvatar(t, color.NRGBA{B: 210, A: 255}),
	}, nil)
	_, codeAndFooter, found := strings.Cut(sent, "你的注册验证码是：")
	code, _, _ := strings.Cut(codeAndFooter, "\r\n")
	if status != 202 || !found || len(code) != 6 {
		t.Fatalf("challenge creation returned %d", status)
	}
	status, out, response := e.request(http.MethodPost, "kimnotes/auth/verify", map[string]any{
		"challengeId": pending["challengeId"], "code": code,
	}, nil)
	if status != 200 {
		t.Fatalf("avatar verification status %d: %v", status, out)
	}
	avatarURL := assertNoteAvatarURL(t, out, "头像小羊")
	if code, _, _ := requestNoteAvatar(t, e, avatarURL, response.Cookies()[0]); code != 200 {
		t.Fatalf("verified custom avatar returned %d", code)
	}
}

func assertNoteAvatarURL(t *testing.T, out map[string]any, name string) string {
	t.Helper()
	user := assertNoteProfile(t, out, name, defaultNoteAvatar)
	url, _ := user["avatarUrl"].(string)
	version, ok := strings.CutPrefix(url, noteAvatarPath)
	if !ok || !validID(version) {
		t.Fatalf("invalid custom avatar URL: %q", url)
	}
	return url
}

func requestNoteAvatar(t *testing.T, e *testEnv, url string, cookie *http.Cookie) (int, http.Header, []byte) {
	t.Helper()
	request, err := http.NewRequest(http.MethodGet, e.server.URL+url, nil)
	if err != nil {
		t.Fatal(err)
	}
	if cookie != nil {
		request.AddCookie(cookie)
	}
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	return response.StatusCode, response.Header, body
}
