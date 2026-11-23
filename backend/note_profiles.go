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

type noteProfileUpdate struct {
	noteProfile
	AvatarImage string `json:"avatarImage"`
	avatarPNG   []byte
}

const defaultNoteNickname = "小羊用户"
const defaultNoteAvatar = "sheep"

func (p *noteProfile) validate() error {
	p.Nickname = strings.TrimSpace(p.Nickname)
	if n := utf8.RuneCountInString(p.Nickname); n < 1 || n > 24 || strings.ContainsFunc(p.Nickname, unicode.IsControl) {
		return fail(400, "invalid_nickname", "请输入 1 至 24 个字符的昵称。")
	}
	switch p.AvatarID {
	case "sheep", "sky", "mint", "peach", "lilac", "gray", "custom":
		return nil
	default:
		return fail(400, "invalid_avatar", "请选择头像。")
	}
}

func (p *noteProfileUpdate) prepare(allowRetain bool) error {
	if err := p.noteProfile.validate(); err != nil {
		return err
	}
	if p.AvatarID != "custom" {
		if p.AvatarImage != "" {
			return fail(400, "invalid_avatar", "请选择上传的头像或一个预设头像。")
		}
		return nil
	}
	if p.AvatarImage == "" {
		if allowRetain {
			return nil
		}
		return fail(400, "invalid_avatar", "请上传头像图片。")
	}
	var err error
	p.avatarPNG, err = normalizeNoteAvatar(p.AvatarImage)
	return err
}

func (p noteProfileUpdate) preset() string {
	if p.AvatarID == "custom" {
		return defaultNoteAvatar
	}
	return p.AvatarID
}

func registrationNoteProfile(in credentials) (noteProfileUpdate, error) {
	if in.NoteProfileVersion == 0 && in.NoteAvatarID == "" && in.NoteAvatarImage == "" {
		return noteProfileUpdate{}, nil
	}
	if in.NoteProfileVersion != 0 && in.NoteProfileVersion != 1 {
		return noteProfileUpdate{}, fail(400, "invalid_profile_version", "请刷新注册页面后重试。")
	}
	profile := noteProfileUpdate{noteProfile: noteProfile{Nickname: in.Nickname, AvatarID: in.NoteAvatarID}, AvatarImage: in.NoteAvatarImage}
	err := profile.prepare(false)
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
	var avatarVersion string
	err := db.QueryRow(ctx, `SELECT nickname,avatar_id,COALESCE(avatar_version::text,'') FROM note_profiles WHERE app_id=$1 AND user_id=$2`, app, id).
		Scan(&profile.Nickname, &profile.AvatarID, &avatarVersion)
	if errors.Is(err, pgx.ErrNoRows) {
		profile = noteProfile{Nickname: defaultNoteNickname, AvatarID: defaultNoteAvatar}
	} else if err != nil {
		return nil, err
	}
	setNoteProfile(user, profile, avatarVersion)
	return user, nil
}

func setNoteProfile(user map[string]any, profile noteProfile, avatarVersion string) {
	user["nickname"], user["avatarId"] = profile.Nickname, profile.AvatarID
	if avatarVersion != "" {
		user["avatarUrl"] = "/api/v1/apps/kimnotes/profile/avatar?v=" + avatarVersion
	}
}

func saveNewNoteProfile(ctx context.Context, tx pgx.Tx, id string, profile noteProfileUpdate) error {
	if profile.Nickname == "" {
		profile.noteProfile = noteProfile{Nickname: defaultNoteNickname, AvatarID: defaultNoteAvatar}
	}
	var avatarVersion string
	if profile.avatarPNG != nil {
		avatarVersion = newID()
	}
	_, err := tx.Exec(ctx, `INSERT INTO note_profiles(user_id,nickname,avatar_id,avatar_png,avatar_version)
		VALUES($1,$2,$3,$4,NULLIF($5,'')::uuid)`, id, profile.Nickname, profile.preset(), profile.avatarPNG, avatarVersion)
	return err
}

func (a *App) putNoteProfile(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	if p.AppID != "kimnotes" {
		return fail(404, "not_found", "内容不存在或已不可用。")
	}
	var profile noteProfileUpdate
	if err := decode(w, r, &profile); err != nil {
		return err
	}
	if err := profile.prepare(true); err != nil {
		return err
	}
	ctx := r.Context()
	var avatarVersion string
	if profile.AvatarID == "custom" && profile.avatarPNG == nil {
		// Only change the nickname if there is still a custom avatar to keep.
		err := a.db.QueryRow(ctx, `UPDATE note_profiles SET nickname=$3 WHERE app_id=$1 AND user_id=$2 AND avatar_version IS NOT NULL
			RETURNING avatar_version::text`, p.AppID, p.UserID, profile.Nickname).Scan(&avatarVersion)
		if errors.Is(err, pgx.ErrNoRows) {
			return fail(409, "avatar_missing", "自定义头像已更换，请重新选择图片。")
		}
		if err != nil {
			return err
		}
	} else {
		if profile.avatarPNG != nil {
			avatarVersion = newID()
		}
		_, err := a.db.Exec(ctx, `INSERT INTO note_profiles (app_id,user_id,nickname,avatar_id,avatar_png,avatar_version)
			VALUES($1,$2,$3,$4,$5,NULLIF($6,'')::uuid)
			ON CONFLICT (app_id,user_id) DO UPDATE SET nickname=EXCLUDED.nickname,avatar_id=EXCLUDED.avatar_id,
			avatar_png=EXCLUDED.avatar_png,avatar_version=EXCLUDED.avatar_version`,
			p.AppID, p.UserID, profile.Nickname, profile.preset(), profile.avatarPNG, avatarVersion)
		if err != nil {
			return err
		}
	}
	user := map[string]any{"id": p.UserID, "email": p.Email, "emailVerified": p.Verified}
	setNoteProfile(user, noteProfile{Nickname: profile.Nickname, AvatarID: profile.preset()}, avatarVersion)
	writeJSON(w, 200, map[string]any{"user": user})
	return nil
}

func (a *App) getNoteAvatar(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	version := r.URL.Query().Get("v")
	if p.AppID != "kimnotes" || !validID(version) {
		return fail(404, "not_found", "头像不存在。")
	}
	var png []byte
	err := a.db.QueryRow(r.Context(), `SELECT avatar_png FROM note_profiles WHERE app_id=$1 AND user_id=$2 AND avatar_version=$3`,
		p.AppID, p.UserID, version).Scan(&png)
	if err != nil {
		return err
	}
	w.Header().Set("Content-Type", "image/png")
	_, err = w.Write(png)
	return err
}
