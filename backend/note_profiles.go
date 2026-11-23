package main

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/jackc/pgx/v5"
)

type noteProfile struct {
	Nickname string `json:"nickname"`
	AvatarID string `json:"avatarId"`
}

const defaultNoteNickname = "小羊用户"
const defaultNoteAvatar = "sheep"

func (p *noteProfile) validate() error {
	p.Nickname = strings.TrimSpace(p.Nickname)
	if n := utf8.RuneCountInString(p.Nickname); n < 1 || n > 24 || strings.ContainsFunc(p.Nickname, unicode.IsControl) {
		return fail(400, "invalid_nickname", "请输入 1 至 24 个字符的昵称。")
	}
	switch p.AvatarID {
	case "sheep", "sky", "mint", "peach", "lilac", "gray":
		return nil
	default:
		return fail(400, "invalid_avatar", "请选择头像。")
	}
}

func registrationNoteProfile(in credentials) (noteProfile, error) {
	if in.NoteProfileVersion == 0 && in.NoteAvatarID == "" {
		return noteProfile{}, nil
	}
	if in.NoteProfileVersion != 0 && in.NoteProfileVersion != 1 {
		return noteProfile{}, fail(400, "invalid_profile_version", "请刷新注册页面后重试。")
	}
	profile := noteProfile{Nickname: in.Nickname, AvatarID: in.NoteAvatarID}
	err := profile.validate()
	return profile, err
}

type noteProfileReader interface {
	QueryRow(context.Context, string, ...any) pgx.Row
}

func noteUser(ctx context.Context, db noteProfileReader, app, id, email string, verified bool) (map[string]any, error) {
	user := map[string]any{"id": id, "email": email, "emailVerified": verified}
	if app != "kimnotes" {
		return user, nil
	}
	var profile noteProfile
	err := db.QueryRow(ctx, "SELECT nickname,avatar_id FROM note_profiles WHERE app_id=$1 AND user_id=$2", app, id).
		Scan(&profile.Nickname, &profile.AvatarID)
	if errors.Is(err, pgx.ErrNoRows) {
		profile = noteProfile{Nickname: defaultNoteNickname, AvatarID: defaultNoteAvatar}
	} else if err != nil {
		return nil, err
	}
	user["nickname"], user["avatarId"] = profile.Nickname, profile.AvatarID
	return user, nil
}

func saveNewNoteProfile(ctx context.Context, tx pgx.Tx, id string, profile noteProfile) error {
	if profile.Nickname == "" {
		profile = noteProfile{Nickname: defaultNoteNickname, AvatarID: defaultNoteAvatar}
	}
	_, err := tx.Exec(ctx, "INSERT INTO note_profiles(user_id,nickname,avatar_id) VALUES($1,$2,$3)", id, profile.Nickname, profile.AvatarID)
	return err
}

func (a *App) putNoteProfile(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.AppID != "kimnotes" {
		return fail(404, "not_found", "内容不存在或已不可用。")
	}
	var profile noteProfile
	if err := decode(w, r, &profile); err != nil {
		return err
	}
	if err := profile.validate(); err != nil {
		return err
	}
	_, err := a.db.Exec(r.Context(), `INSERT INTO note_profiles (app_id,user_id,nickname,avatar_id) VALUES($1,$2,$3,$4)
		ON CONFLICT (app_id,user_id) DO UPDATE SET nickname=EXCLUDED.nickname,avatar_id=EXCLUDED.avatar_id`,
		p.AppID, p.UserID, profile.Nickname, profile.AvatarID)
	if err != nil {
		return err
	}
	writeJSON(w, 200, map[string]any{"user": map[string]any{
		"id": p.UserID, "email": p.Email, "emailVerified": p.Verified, "nickname": profile.Nickname, "avatarId": profile.AvatarID,
	}})
	return nil
}
