package main

import (
	"context"
	"net/http"
	"strings"
	"testing"
)

func TestNoteProfileIsAppScoped(t *testing.T) {
	e := guitarTestEnv(t)
	const password = "Test-password-123"
	name := strings.Repeat("羊", 24)
	in := credentials{Email: "note-profile@example.com", Password: password, NoteProfileVersion: 1, Nickname: name}
	if status, _, _ := e.request(http.MethodPost, "kimnotes/auth/register", in, nil); status != 400 {
		t.Fatalf("registration accepted an absent avatar: %d", status)
	}
	in.NoteAvatarID, in.Nickname = "mint", name+"羊"
	if status, _, _ := e.request(http.MethodPost, "kimnotes/auth/register", in, nil); status != 400 {
		t.Fatalf("registration accepted an overlong nickname: %d", status)
	}
	in.Nickname = "  " + name + "  "
	status, out, res := e.request(http.MethodPost, "kimnotes/auth/register", in, nil)
	if status != 200 {
		t.Fatalf("registration status %d: %v", status, out)
	}
	user := assertNoteProfile(t, out, name, "mint")
	status, out, _ = e.request(http.MethodGet, "kimnotes/auth/me", nil, res.Cookies()[0])
	if status != 200 {
		t.Fatalf("notes profile status %d", status)
	}
	assertNoteProfile(t, out, name, "mint")
	for _, app := range []string{"main", "guitar"} {
		status, out, res = e.request(http.MethodPost, app+"/auth/login", credentials{Email: in.Email, Password: password}, nil)
		assertAppIdentity(t, app, status, out, user["id"])
		status, out, _ = e.request(http.MethodGet, app+"/auth/me", nil, res.Cookies()[0])
		assertAppIdentity(t, app, status, out, user["id"])
		if status, _, _ := e.request(http.MethodPut, app+"/profile", noteProfile{"无效", "gray"}, res.Cookies()[0]); status != 404 {
			t.Fatalf("%s session can write a Notes profile: %d", app, status)
		}
	}
	pair := e.desktop("note-profile", true)
	assertNoteProfile(t, pair, name, "mint")
	status, out = e.refresh(pair, newID())
	if status != 200 {
		t.Fatalf("desktop refresh status %d: %v", status, out)
	}
	assertNoteProfile(t, out, name, "mint")
}

func TestNoteProfileDefaultsAndUpdate(t *testing.T) {
	e := guitarTestEnv(t)
	const password = "Test-password-123"
	const email = "guitar-profile-default@example.com"
	status, out, _ := e.request(http.MethodPost, "guitar/auth/register", credentials{
		Email: email, Password: password, Nickname: "琴友小羊", Avatar: "wave",
	}, nil)
	guitarUser, _ := out["user"].(map[string]any)
	assertAppIdentity(t, "guitar", status, out, guitarUser["id"])
	var nick, avatar string
	if err := e.db.QueryRow(context.Background(), `SELECT nickname,avatar FROM guitar_members WHERE user_id=$1`, guitarUser["id"]).Scan(&nick, &avatar); err != nil || nick != "琴友小羊" || avatar != "wave" {
		t.Fatalf("legacy guitar member profile %q/%q: %v", nick, avatar, err)
	}
	status, out, res := e.request(http.MethodPost, "kimnotes/auth/login", credentials{Email: email, Password: password}, nil)
	if status != 200 || assertNoteProfile(t, out, defaultNoteNickname, defaultNoteAvatar)["id"] != guitarUser["id"] {
		t.Fatalf("first Notes login did not assign its defaults: %d", status)
	}
	cookie := res.Cookies()[0]
	if status, _, _ := e.request(http.MethodPut, "kimnotes/profile", noteProfile{"昵称", "outside-url"}, cookie); status != 400 {
		t.Fatalf("invalid avatar update returned %d", status)
	}
	status, out, _ = e.request(http.MethodPut, "kimnotes/profile", noteProfile{"  晴天  ", "sky"}, cookie)
	if status != 200 {
		t.Fatalf("profile update status %d: %v", status, out)
	}
	assertNoteProfile(t, out, "晴天", "sky")
	status, out, _ = e.request(http.MethodGet, "kimnotes/auth/me", nil, cookie)
	if status != 200 {
		t.Fatalf("updated Notes profile status %d", status)
	}
	assertNoteProfile(t, out, "晴天", "sky")
	if err := e.db.QueryRow(context.Background(), `SELECT nickname,avatar FROM guitar_members WHERE user_id=$1`, guitarUser["id"]).Scan(&nick, &avatar); err != nil || nick != "琴友小羊" || avatar != "wave" {
		t.Fatalf("Notes profile update changed guitar member %q/%q: %v", nick, avatar, err)
	}
	legacy := e.account("legacy-note-profile")
	status, out, _ = e.request(http.MethodGet, "kimnotes/auth/me", nil, legacy)
	if status != 200 {
		t.Fatalf("legacy Notes registration lost compatibility: %d", status)
	}
	assertNoteProfile(t, out, defaultNoteNickname, defaultNoteAvatar)
}

func TestNoteProfileSurvivesEmailVerification(t *testing.T) {
	e := newTestEnv(t)
	e.app.config.RequireVerification = true
	var sent string
	e.app.sendMail = func(_ context.Context, _, _, body string) error { sent = body; return nil }
	status, pending, _ := e.request(http.MethodPost, "kimnotes/auth/register", credentials{
		Email: "verified-profile@example.com", Password: "Test-password-123", NoteProfileVersion: 1,
		Nickname: "晴天", NoteAvatarID: "sky",
	}, nil)
	_, codeAndFooter, found := strings.Cut(sent, "你的注册验证码是：")
	code, _, _ := strings.Cut(codeAndFooter, "\r\n")
	if status != 202 || !found || len(code) != 6 {
		t.Fatalf("challenge creation returned %d", status)
	}
	status, out, _ := e.request(http.MethodPost, "kimnotes/auth/verify", map[string]any{
		"challengeId": pending["challengeId"], "code": code,
	}, nil)
	if status != 200 || assertNoteProfile(t, out, "晴天", "sky")["emailVerified"] != true {
		t.Fatalf("verified account is missing its Notes profile: %d", status)
	}
}

func TestNoteProfileMigrationBackfillsExistingMemberships(t *testing.T) {
	e := newTestEnv(t)
	notes := e.account("existing-notes-profile")
	status, out, _ := e.request(http.MethodPost, "main/auth/register", credentials{
		Email: "existing-main-profile@example.com", Password: "Test-password-123",
	}, nil)
	if status != 200 {
		t.Fatalf("main-only registration returned %d", status)
	}
	mainID := out["user"].(map[string]any)["id"].(string)
	status, out, _ = e.request(http.MethodGet, "kimnotes/auth/me", nil, notes)
	if status != 200 {
		t.Fatalf("Notes identity returned %d", status)
	}
	notesID := out["user"].(map[string]any)["id"].(string)

	// Recreate the pre-migration state inside the isolated schema transaction.
	ctx := context.Background()
	tx, err := e.db.Begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback(ctx)
	if _, err = tx.Exec(ctx, `DROP TABLE note_profiles;
		ALTER TABLE auth_challenges DROP COLUMN note_nickname, DROP COLUMN note_avatar_id`); err != nil {
		t.Fatal(err)
	}
	if _, err = tx.Exec(ctx, migrationFixture("040_note_profiles")); err != nil {
		t.Fatal(err)
	}
	var nickname, avatar string
	if err = tx.QueryRow(ctx, "SELECT nickname,avatar_id FROM note_profiles WHERE user_id=$1", notesID).Scan(&nickname, &avatar); err != nil || nickname != defaultNoteNickname || avatar != defaultNoteAvatar {
		t.Fatalf("existing Notes membership lacks persisted default %q/%q: %v", nickname, avatar, err)
	}
	var count int
	if err = tx.QueryRow(ctx, "SELECT count(*) FROM note_profiles WHERE user_id=$1", mainID).Scan(&count); err != nil || count != 0 {
		t.Fatalf("main-only membership received a Notes profile: %d, %v", count, err)
	}
}

func assertNoteProfile(t *testing.T, out map[string]any, nickname, avatar string) map[string]any {
	t.Helper()
	user, ok := out["user"].(map[string]any)
	if !ok || user["nickname"] != nickname || user["avatarId"] != avatar {
		t.Fatalf("expected Notes profile %q/%q, got %v", nickname, avatar, user)
	}
	return user
}

func assertAppIdentity(t *testing.T, app string, status int, out map[string]any, id any) {
	t.Helper()
	user, ok := out["user"].(map[string]any)
	if status != 200 || !ok || user["id"] != id || user["nickname"] != nil || user["avatarId"] != nil {
		t.Fatalf("%s auth response changed or leaked Notes profile: %d, %v", app, status, out)
	}
}
