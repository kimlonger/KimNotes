package main

import (
	"strings"
	"time"
)

// The timestamp lives alongside the document so old desktop clients can keep
// syncing without a schema upgrade. Only the server assigns it; a client's
// copy (or omission) never changes an unchanged note's content time.
func contentTime(previous Note, next Document, now time.Time) *time.Time {
	modified := now.UTC()
	if !previous.UpdatedAt.IsZero() && sameContent(previous.Document, next) {
		modified = previous.UpdatedAt
		if previous.Document.ContentUpdatedAt != nil {
			modified = *previous.Document.ContentUpdatedAt
		}
	}
	return &modified
}

func sameContent(a, b Document) bool {
	if a.Title != b.Title || a.Text != b.Text || len(a.Attachments) != len(b.Attachments) {
		return false
	}
	for i := range a.Attachments {
		if a.Attachments[i] != b.Attachments[i] {
			return false
		}
	}
	ar, ae := contentRTF(a)
	br, be := contentRTF(b)
	return ae == nil && be == nil && ar == br
}

func contentRTF(d Document) (string, error) {
	rtf, err := unpackRichText(d.RTF, d.RTFGzip)
	if err != nil {
		return "", err
	}
	if len(d.RTFImages) == 0 {
		return rtf, nil
	}
	images := make(map[string]RTFImage, len(d.RTFImages))
	for _, image := range d.RTFImages {
		images[image.Marker] = image
	}
	// Export gives each image a fresh marker. Compare its attachment and size,
	// rather than treating the newly generated marker as an image edit.
	return inlineMarker.ReplaceAllStringFunc(rtf, func(marker string) string {
		id := inlineMarker.FindStringSubmatch(marker)[1]
		image, ok := images[id]
		if !ok {
			return marker
		}
		return strings.Join([]string{"{image:", image.AttachmentID, ":", image.Properties, "}"}, "")
	}), nil
}
