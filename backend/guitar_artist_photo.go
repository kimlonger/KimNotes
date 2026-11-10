package main

import (
	"bytes"
	"context"
	"fmt"
	"image"
	"io"
	"net/http"
	"os"

	"github.com/jackc/pgx/v5/pgxpool"
)

const guitarArtistAvatarURL = `COALESCE('/api/v1/apps/guitar/artists/'||a.id||'/avatar?v='||extract(epoch FROM a.avatar_updated_at)::text,'')`

func guitarSaveArtistPhoto(ctx context.Context, db *pgxpool.Pool, args []string) error {
	if len(args) != 2 || !validID(args[0]) {
		return fmt.Errorf("usage: guitar-review artist-photo <artist-id> <image-file>")
	}
	file, err := os.Open(args[1])
	if err != nil {
		return err
	}
	defer file.Close()
	const maxBytes = 5 << 20
	data, err := io.ReadAll(io.LimitReader(file, maxBytes+1))
	if err != nil {
		return err
	}
	if len(data) > maxBytes {
		return fmt.Errorf("artist image must be at most 5 MiB")
	}
	mediaType := http.DetectContentType(data)
	if mediaType != "image/png" && mediaType != "image/jpeg" && mediaType != "image/webp" {
		return fmt.Errorf("artist image must be PNG, JPEG or WebP")
	}
	config, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil || config.Width < 1 || config.Height < 1 || int64(config.Width)*int64(config.Height) > 24_000_000 {
		return fmt.Errorf("artist image is invalid or exceeds 24 megapixels")
	}
	if _, _, err := image.Decode(bytes.NewReader(data)); err != nil {
		return fmt.Errorf("invalid artist image: %w", err)
	}
	tag, err := db.Exec(ctx, `UPDATE guitar_artists SET avatar_bytes=$2,avatar_mime=$3,avatar_updated_at=now() WHERE app_id='guitar' AND id=$1`, args[0], data, mediaType)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("artist not found")
	}
	return nil
}

func (a *App) guitarArtistAvatar(w http.ResponseWriter, r *http.Request) error {
	id := r.PathValue("artist")
	if !validID(id) {
		return fail(404, "not_found", "歌手图片不存在。")
	}
	var data []byte
	var mediaType string
	if err := a.db.QueryRow(r.Context(), `SELECT avatar_bytes,avatar_mime FROM guitar_artists WHERE app_id='guitar' AND id=$1 AND avatar_bytes IS NOT NULL`, id).Scan(&data, &mediaType); err != nil {
		return err
	}
	w.Header().Set("Content-Type", mediaType)
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	_, err := w.Write(data)
	return err
}
