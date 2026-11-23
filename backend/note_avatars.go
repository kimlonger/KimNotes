package main

import (
	"bytes"
	"encoding/base64"
	"image"
	_ "image/jpeg"
	"image/png"

	"golang.org/x/image/draw"
	_ "golang.org/x/image/webp"
)

const (
	noteAvatarLimit     = 512 << 10
	noteAvatarInputSide = 2048
	noteAvatarSide      = 256
)

func normalizeNoteAvatar(input string) ([]byte, error) {
	if len(input) > base64.StdEncoding.EncodedLen(noteAvatarLimit) {
		return nil, fail(400, "invalid_avatar", "头像图片不能超过 512 KB。")
	}
	raw, err := base64.StdEncoding.DecodeString(input)
	if err != nil || len(raw) == 0 || len(raw) > noteAvatarLimit {
		return nil, fail(400, "invalid_avatar", "头像图片无效或超过 512 KB。")
	}
	config, format, err := image.DecodeConfig(bytes.NewReader(raw))
	if err != nil || format != "png" && format != "jpeg" && format != "webp" {
		return nil, fail(400, "invalid_avatar", "请上传 PNG、JPG 或 WebP 图片。")
	}
	if config.Width < 1 || config.Width > noteAvatarInputSide || config.Height < 1 || config.Height > noteAvatarInputSide {
		return nil, fail(400, "invalid_avatar", "头像宽度和高度均不能超过 2048 像素。")
	}
	source, _, err := image.Decode(bytes.NewReader(raw))
	if err != nil || source.Bounds().Size() != (image.Point{X: config.Width, Y: config.Height}) {
		return nil, fail(400, "invalid_avatar", "图片损坏，无法读取头像。")
	}
	// Store a consistent square bitmap without the uploaded file's metadata.
	side := min(config.Width, config.Height)
	left := source.Bounds().Min.X + (config.Width-side)/2
	top := source.Bounds().Min.Y + (config.Height-side)/2
	crop := image.Rect(left, top, left+side, top+side)
	avatar := image.NewNRGBA(image.Rect(0, 0, noteAvatarSide, noteAvatarSide))
	draw.CatmullRom.Scale(avatar, avatar.Bounds(), source, crop, draw.Src, nil)
	var out bytes.Buffer
	if err := png.Encode(&out, avatar); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}
