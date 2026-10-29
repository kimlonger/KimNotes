package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"image"
	"image/png"
	"io"
	"net/http"
	"strings"
	"testing"
)

func inlineDocument(id string) Document {
	marker := newID()
	return Document{Title: "before", Text: "before\nafter", RTFGzip: packedRTF(`{\rtf1\ansi\b before\b0 {\*\kimimage ` + marker + `}\i after\i0}`), Attachments: []string{id}, RTFImages: []RTFImage{{Marker: marker, AttachmentID: id, Properties: `\picwgoal6000\pichgoal4000\picscalex100\piccropl0`}}}
}
func TestInlineImageValidation(t *testing.T) {
	valid := inlineDocument(newID())
	if e := valid.validate(); e != nil {
		t.Fatal(e)
	}
	// Content and markers may be plain legacy RTF too, but never half-resolved.
	plain := valid
	plain.RTF, _ = unpackRichText("", plain.RTFGzip)
	plain.RTFGzip = ""
	if e := plain.validate(); e != nil {
		t.Fatal(e)
	}
	cases := []struct {
		name   string
		mutate func(*Document)
	}{
		{"missing refs", func(d *Document) { d.RTFImages = nil }},
		{"missing marker", func(d *Document) { d.RTFGzip = packedRTF(`{\rtf1 body}`) }},
		{"duplicate refs", func(d *Document) { d.RTFImages = append(d.RTFImages, d.RTFImages[0]) }},
		{"duplicate markers", func(d *Document) {
			d.RTFGzip = packedRTF(`{\rtf1{\*\kimimage ` + d.RTFImages[0].Marker + `}{\*\kimimage ` + d.RTFImages[0].Marker + `}}`)
		}},
		{"unused marker", func(d *Document) {
			d.RTFGzip = packedRTF(`{\rtf1{\*\kimimage ` + d.RTFImages[0].Marker + `}{\*\kimimage ` + newID() + `}}`)
		}},
		{"unlisted attachment", func(d *Document) { d.Attachments = nil }},
		{"invalid UUID", func(d *Document) { d.RTFImages[0].AttachmentID = "../x" }},
		{"executable properties", func(d *Document) { d.RTFImages[0].Properties = `\object\objdata0` }},
		{"duplicate properties", func(d *Document) { d.RTFImages[0].Properties = `\picwgoal1\picwgoal2` }},
		{"negative goal", func(d *Document) { d.RTFImages[0].Properties = `\picwgoal-2` }},
		{"zero scale", func(d *Document) { d.RTFImages[0].Properties = `\picscalex0` }},
		{"extreme crop", func(d *Document) { d.RTFImages[0].Properties = `\piccropl9999999` }},
		{"mixed inline bytes", func(d *Document) {
			r, _ := unpackRichText("", d.RTFGzip)
			d.RTFGzip = packedRTF(strings.TrimSuffix(r, "}") + `{\pict\pngblip 00}}`)
		}},
		{"unbalanced", func(d *Document) { r, _ := unpackRichText("", d.RTFGzip); d.RTFGzip = packedRTF(r + "}") }},
		{"nested bomb", func(d *Document) {
			r, _ := unpackRichText("", d.RTFGzip)
			d.RTFGzip = packedRTF(r[:len(r)-1] + strings.Repeat("{", 300) + strings.Repeat("}", 300) + "}")
		}},
		{"binary payload", func(d *Document) {
			r, _ := unpackRichText("", d.RTFGzip)
			d.RTFGzip = packedRTF(r[:len(r)-1] + `\bin4 exe!}`)
		}},
		{"unknown marker syntax", func(d *Document) {
			r, _ := unpackRichText("", d.RTFGzip)
			d.RTFGzip = packedRTF(r[:len(r)-1] + `{\*\kimimage BAD}}`)
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			d := inlineDocument(newID())
			c.mutate(&d)
			if d.validate() == nil {
				t.Fatal("malformed template accepted")
			}
		})
	}
}
func TestInlineImagePrivateRoundTrip(t *testing.T) {
	e := newTestEnv(t)
	one := e.account("inline-owner")
	two := e.account("inline-other")
	upload := func(img image.Image) (string, []byte) {
		var data bytes.Buffer
		if err := png.Encode(&data, img); err != nil {
			t.Fatal(err)
		}
		r, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/kimnotes/attachments", bytes.NewReader(data.Bytes()))
		r.Header.Set("Origin", "https://notes.test")
		r.Header.Set("Content-Type", "image/png")
		r.AddCookie(one)
		res, err := http.DefaultClient.Do(r)
		if err != nil {
			t.Fatal(err)
		}
		defer res.Body.Close()
		var out map[string]any
		_ = json.NewDecoder(res.Body).Decode(&out)
		if res.StatusCode != 201 {
			t.Fatal(res.StatusCode, out)
		}
		return out["id"].(string), data.Bytes()
	}
	// Deterministic RGB noise: the old embedded PNG hex exceeds the 2 MiB packed
	// RTF limit although this original image is safely below the 10 MiB quota.
	img := image.NewNRGBA(image.Rect(0, 0, 1100, 800))
	var seed uint32 = 0x1234abcd
	for i := 0; i < len(img.Pix); i += 4 {
		for c := 0; c < 3; c++ {
			seed ^= seed << 13
			seed ^= seed >> 17
			seed ^= seed << 5
			img.Pix[i+c] = byte(seed)
		}
		img.Pix[i+3] = 255
	}
	id, raw := upload(img)
	legacy := Document{RTFGzip: packedRTF(`{\rtf1{\pict\pngblip ` + hex.EncodeToString(raw) + `}}`)}
	if legacy.validate() == nil {
		t.Fatal("fixture must reproduce old packed-image limit")
	}
	doc := inlineDocument(id)
	n := result(e.push(one, Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &doc}))["note"].(map[string]any)
	stored := n["document"].(map[string]any)
	if len(stored["rtfImages"].([]any)) != 1 || len(stored["rtfGzip"].(string)) > 2048 {
		t.Fatal("image bytes duplicated into template")
	}
	status, pull, _ := e.request("GET", "kimnotes/sync/pull?cursor=0", nil, one)
	if status != 200 || len(pull["changes"].([]any)) != 1 {
		t.Fatal(status, pull)
	}
	pair := e.desktop("inline-owner", true)
	legacyHeaders := map[string]string{"X-Kim-Client": "desktop"}
	newHeaders := map[string]string{"X-Kim-Client": "desktop", "X-Kim-Inline-Images": "1"}
	for _, path := range []string{"kimnotes/sync/pull?cursor=0", "kimnotes/sync/bootstrap"} {
		if s, out, _ := e.sessionRequest("GET", path, nil, nil, pair["token"].(string), legacyHeaders); s != 426 || out["code"] != "client_upgrade_required" {
			t.Fatal("old desktop must not receive silently missing images", path, s, out)
		}
		if s, out, _ := e.sessionRequest("GET", path, nil, nil, pair["token"].(string), newHeaders); s != 200 {
			t.Fatal("capable desktop cannot receive image template", path, s, out)
		}
	}
	oldBatch := []Operation{
		{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &Document{Title: "must not partially commit"}},
		{OperationID: newID(), NoteID: n["id"].(string), BaseRevision: 1, Action: "upsert", Document: &Document{Title: "must not erase inline images"}},
	}
	if s, out, _ := e.sessionRequest("POST", "kimnotes/sync/push", map[string]any{"operations": oldBatch}, nil, pair["token"].(string), legacyHeaders); s != 426 || out["code"] != "client_upgrade_required" {
		t.Fatal("old desktop must not overwrite image templates", s, out)
	}
	var noteCount int
	if err := e.db.QueryRow(context.Background(), "SELECT count(*) FROM notes").Scan(&noteCount); err != nil || noteCount != 1 {
		t.Fatal("old multi-operation push partially committed", noteCount, err)
	}
	if s, out, _ := e.sessionRequest("GET", "kimnotes/notes/"+n["id"].(string)+"/versions", nil, nil, pair["token"].(string), legacyHeaders); s != 426 {
		t.Fatal("old desktop received unreadable image history", s, out)
	}
	req, _ := http.NewRequest("GET", e.server.URL+"/api/v1/apps/kimnotes/attachments/"+id, nil)
	req.AddCookie(one)
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	downloaded, _ := io.ReadAll(res.Body)
	res.Body.Close()
	if res.StatusCode != 200 || !bytes.Equal(downloaded, raw) {
		t.Fatal("private original changed")
	}
	op := Operation{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &doc}
	if status, _, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": []Operation{op}}, two); status != 400 {
		t.Fatal("cross-user image template accepted", status)
	}
	if _, err := e.db.Exec(context.Background(), "INSERT INTO applications(id,name) VALUES('another-app','Another')"); err != nil {
		t.Fatal(err)
	}
	_, _, login := e.request("POST", "another-app/auth/login", credentials{Email: "inline-owner@example.com", Password: "Test-password-123"}, nil)
	var other *http.Cookie
	for _, cookie := range login.Cookies() {
		if strings.Contains(cookie.Name, "another-app") {
			other = cookie
		}
	}
	if other == nil {
		t.Fatal("other app fixture")
	}
	if status, _, _ := e.request("POST", "another-app/sync/push", map[string]any{"operations": []Operation{op}}, other); status != 400 {
		t.Fatal("cross-app image template accepted", status)
	}
	// A highly compressed, larger raster is individually valid, but reconstructing
	// it as hex/WMF would exceed the document budget. Reject before clients allocate.
	hugeID, _ := upload(image.NewRGBA(image.Rect(0, 0, 4200, 4200)))
	huge := inlineDocument(hugeID)
	if status, out, _ := e.request("POST", "kimnotes/sync/push", map[string]any{"operations": []Operation{{OperationID: newID(), NoteID: newID(), Action: "upsert", Document: &huge}}}, one); status != 413 || out["code"] != "rtf_image_limit" {
		t.Fatal("decoded pixel budget not enforced", status, out)
	}
}
