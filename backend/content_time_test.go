package main

import (
	"encoding/json"
	"net/http"
	"testing"
	"time"
)

func TestContentTimeIgnoresMetadataAndClientClock(t *testing.T) {
	created := time.Date(2026, 9, 30, 8, 0, 0, 0, time.UTC)
	edited := created.Add(time.Hour)
	now := created.Add(24 * time.Hour)
	previous := Note{CreatedAt: created, UpdatedAt: now, Document: Document{Title: "title", Text: "text", ContentUpdatedAt: &edited}}
	forged := now.Add(365 * 24 * time.Hour)
	next := previous.Document
	next.ThemeID = 4
	next.ContentUpdatedAt = &forged
	if got := contentTime(previous, next, now); !got.Equal(edited) {
		t.Fatal("theme or client timestamp changed the content time", got)
	}
	next.ContentUpdatedAt = nil // Existing desktop clients don't send this metadata.
	if got := contentTime(previous, next, now); !got.Equal(edited) {
		t.Fatal("legacy client lost the content time", got)
	}
	next.Text += " edited"
	if got := contentTime(previous, next, now); !got.Equal(now) {
		t.Fatal("content edit did not advance time", got)
	}
	if !previous.CreatedAt.Equal(created) || !previous.Document.ContentUpdatedAt.Equal(edited) {
		t.Fatal("time calculation mutated the original note")
	}
}

func TestContentTimeCreationAndLegacyFallback(t *testing.T) {
	now := time.Now().UTC()
	if got := contentTime(Note{}, Document{}, now); !got.Equal(now) {
		t.Fatal("new empty note requires a creation activity time")
	}
	previous := Note{UpdatedAt: now.Add(-time.Hour), Document: Document{Text: "legacy"}}
	if got := contentTime(previous, previous.Document, now); !got.Equal(previous.UpdatedAt) {
		t.Fatal("upgrading an unchanged legacy note must preserve its available time")
	}
}

func TestContentTimeRecognizesRichTextAndImageEdits(t *testing.T) {
	image := "10000000-0000-4000-8000-000000000001"
	a := inlineDocument(image)
	b := inlineDocument(image) // Marker UUIDs are newly generated, contents identical.
	if !sameContent(a, b) {
		t.Fatal("regenerated image markers were mistaken for an edit")
	}
	b.RTFImages[0].Properties = `\picwgoal3000\pichgoal2000`
	if sameContent(a, b) {
		t.Fatal("image resizing was not recognized as an edit")
	}
	plain := Document{Title: "title", Text: "text", RTF: `{\rtf1\b text}`}
	packed := plain
	packed.RTF = ""
	packed.RTFGzip = packedRTF(plain.RTF)
	if !sameContent(plain, packed) {
		t.Fatal("compression format was mistaken for a content edit")
	}
	packed.RTFGzip = packedRTF(`{\rtf1\i text}`)
	if sameContent(plain, packed) {
		t.Fatal("text formatting change was not recognized")
	}
}

func TestContentTimeAPIPersistence(t *testing.T) {
	e := newTestEnv(t)
	cookie := e.account("content-time")
	id := newID()
	push := func(revision int64, document *Document, action string) Note {
		t.Helper()
		result := result(e.push(cookie, Operation{OperationID: newID(), NoteID: id, BaseRevision: revision, Action: action, Document: document}))
		raw, _ := json.Marshal(result["note"])
		var value Note
		if err := json.Unmarshal(raw, &value); err != nil {
			t.Fatal(err)
		}
		return value
	}
	doc := Document{Title: "test", Text: "first", Attachments: []string{}}
	first := push(0, &doc, "upsert")
	if first.Document.ContentUpdatedAt == nil || !first.Document.ContentUpdatedAt.Equal(first.CreatedAt) {
		t.Fatal("creation must record a server-owned content timestamp")
	}
	doc.ThemeID = 5
	second := push(1, &doc, "upsert")
	if !second.Document.ContentUpdatedAt.Equal(*first.Document.ContentUpdatedAt) || !second.CreatedAt.Equal(first.CreatedAt) {
		t.Fatal("theme changed content/creation timestamps")
	}
	doc.Text = "second"
	third := push(2, &doc, "upsert")
	if !third.Document.ContentUpdatedAt.After(*second.Document.ContentUpdatedAt) || !third.CreatedAt.Equal(first.CreatedAt) {
		t.Fatal("editing must advance content time and preserve creation")
	}
	deleted := push(3, nil, "delete")
	restored := push(4, nil, "restore")
	if !deleted.Document.ContentUpdatedAt.Equal(*third.Document.ContentUpdatedAt) || !restored.Document.ContentUpdatedAt.Equal(*third.Document.ContentUpdatedAt) {
		t.Fatal("delete/restore changed content time")
	}
	status, page, _ := e.request(http.MethodGet, "kimnotes/sync/bootstrap", nil, cookie)
	if status != 200 {
		t.Fatal(status, page)
	}
	value := page["notes"].([]any)[0].(map[string]any)
	if value["document"].(map[string]any)["contentUpdatedAt"] == nil {
		t.Fatal("new computers must receive content time with the latest document")
	}
}
