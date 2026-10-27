package main

import (
	"encoding/json"
	"github.com/jackc/pgx/v5"
	"net/http"
	"regexp"
	"strconv"
	"strings"
)

func desktopNeedsInlineImages(r *http.Request) bool {
	return r.Header.Get("X-Kim-Client") == "desktop" && r.Header.Get("X-Kim-Inline-Images") != "1"
}
func inlineUpgradeRequired() error {
	return fail(426, "client_upgrade_required", "此账号含新版图片便签，请更新小羊便签后继续同步；本机与云端原图仍保留。")
}
func checkInlineReader(r *http.Request, raw []byte) error {
	if !desktopNeedsInlineImages(r) {
		return nil
	}
	var note struct {
		Document struct {
			Images []json.RawMessage `json:"rtfImages"`
		} `json:"document"`
	}
	if e := json.Unmarshal(raw, &note); e != nil {
		return e
	}
	if len(note.Document.Images) > 0 {
		return inlineUpgradeRequired()
	}
	return nil
}

// Each marker replaces one complete picture group. Bytes are private attachments,
// not a second copy of WMF/hex data in every version of a note's rich text.
type RTFImage struct {
	Marker       string `json:"marker"`
	AttachmentID string `json:"attachmentId"`
	Properties   string `json:"properties"`
}

var inlineMarker = regexp.MustCompile(`\{\\\*\\kimimage ([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\}`)
var reservedMarker = regexp.MustCompile(`(?i)\\kimimage`)
var inlineProperties = regexp.MustCompile(`^(?:\\(?:picwgoal|pichgoal|picscalex|picscaley|piccropl|piccropr|piccropt|piccropb)-?\d{1,7})*$`)
var inlineProperty = regexp.MustCompile(`\\(picwgoal|pichgoal|picscalex|picscaley|piccropl|piccropr|piccropt|piccropb)(-?\d+)`)
var inlinePicture = regexp.MustCompile(`(?i)\\pict(?:[^a-zA-Z]|$)`)

func (d *Document) validateInlineImages(rtf string) error {
	bad := func() error {
		return fail(400, "invalid_rtf_images", "富文本图片模板无效，图片标记缺失、重复或显示参数无效。")
	}
	if len(d.RTFImages) == 0 {
		if reservedMarker.MatchString(rtf) {
			return bad()
		}
		return nil
	}
	if len(d.RTFImages) > 32 || rtf == "" || inlinePicture.MatchString(rtf) {
		return bad()
	}
	// Templates are balanced RTF groups. Escaped braces never open/close a group.
	depth := 0
	for i := 0; i < len(rtf); i++ {
		if rtf[i] == '\\' {
			i++
			continue
		}
		if rtf[i] == '{' {
			depth++
		}
		if rtf[i] == '}' {
			depth--
			if depth < 0 {
				return bad()
			}
		}
		if depth > 256 {
			return bad()
		}
	}
	if depth != 0 {
		return bad()
	}
	occurrences := map[string]int{}
	// A malicious 32 MiB template can contain hundreds of thousands of markers.
	// The 33rd is enough to reject it without allocating a match per marker.
	matches := inlineMarker.FindAllStringSubmatch(rtf, 33)
	for _, m := range matches {
		occurrences[m[1]]++
	}
	if len(matches) != len(d.RTFImages) || reservedMarker.MatchString(inlineMarker.ReplaceAllString(rtf, "")) {
		return bad()
	}
	attachments := map[string]bool{}
	for _, id := range d.Attachments {
		attachments[id] = true
	}
	seen := map[string]bool{}
	for _, ref := range d.RTFImages {
		if !validID(ref.Marker) || strings.ToLower(ref.Marker) != ref.Marker || !validID(ref.AttachmentID) || !attachments[ref.AttachmentID] || seen[ref.Marker] || occurrences[ref.Marker] != 1 || len(ref.Properties) > 256 || !inlineProperties.MatchString(ref.Properties) {
			return bad()
		}
		seen[ref.Marker] = true
		properties := map[string]bool{}
		for _, m := range inlineProperty.FindAllStringSubmatch(ref.Properties, -1) {
			n, e := strconv.Atoi(m[2])
			if e != nil || properties[m[1]] || n < -1000000 || n > 1000000 || ((strings.HasSuffix(m[1], "goal") || strings.HasPrefix(m[1], "picscale")) && n <= 0) {
				return bad()
			}
			properties[m[1]] = true
		}
	}
	return nil
}

func (a *App) validateInlineImageSizes(r *http.Request, tx pgx.Tx, p principal, d Document) error {
	if len(d.RTFImages) == 0 {
		return nil
	}
	rtf, e := unpackRichText(d.RTF, d.RTFGzip)
	if e != nil {
		return e
	}
	// RichEdit reconstructs lossless WMF/DIB pixels. Bound the entire restored
	// document before clients allocate pixels, including repeated placements.
	budget := int64(len(rtf))
	sizes := map[string]int64{}
	for _, ref := range d.RTFImages {
		pixels, ok := sizes[ref.AttachmentID]
		if !ok {
			var width, height int64
			var size int64
			e = tx.QueryRow(r.Context(), "SELECT width,height,octet_length(bytes) FROM attachments WHERE app_id=$1 AND user_id=$2 AND id=$3", p.AppID, p.UserID, ref.AttachmentID).Scan(&width, &height, &size)
			if e == pgx.ErrNoRows {
				return fail(400, "invalid_attachment", "图片不存在或不属于当前账号。")
			}
			if e != nil {
				return e
			}
			if width < 1 || height < 1 || width*height > 40000000 || size < 1 || size > 10<<20 {
				return fail(400, "invalid_image", "图片无效，超过 10 MB 或 4000 万像素。")
			}
			if width > 32767 || height > 32767 {
				return fail(413, "rtf_image_dimensions", "图片单边超过 32767 像素，暂无法还原为桌面富文本；本机原图仍保留。")
			}
			pixels = width * height
			sizes[ref.AttachmentID] = pixels
		}
		budget += pixels*8 + 65536
		if budget > 128<<20 {
			return fail(413, "rtf_image_limit", "还原后的便签富文本超过 128 MB；本机原图仍保留，请拆分此便签后重试。")
		}
	}
	return nil
}
