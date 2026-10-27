package main

import (
	"bytes"
	"compress/gzip"
	"encoding/base64"
	"strings"
	"testing"
)

func packedRTF(value string) string {
	var buf bytes.Buffer
	writer := gzip.NewWriter(&buf)
	_, _ = writer.Write([]byte(value))
	_ = writer.Close()
	return base64.StdEncoding.EncodeToString(buf.Bytes())
}
func TestCompressedRichText(t *testing.T) {
	valid := packedRTF("{\\rtf1\\ansi hello\\par world}")
	validDocument := Document{Text: "hello", RTFGzip: valid, ThemeID: 2}
	if validDocument.validate() != nil {
		t.Fatal("valid compressed RTF rejected")
	}
	for _, d := range []Document{
		{RTFGzip: "invalid base64"}, {RTFGzip: packedRTF("not RTF")},
		{RTF: "{\\rtf1 old}", RTFGzip: valid},
		{RTFGzip: packedRTF("{\\rtf1{\\object dangerous}}")},
		{RTFGzip: packedRTF("{\\rtf1" + strings.Repeat("x", 33<<20) + "}")},
		{RTF: "{\\rtf1{\\filetbl external}}"},
	} {
		if d.validate() == nil {
			t.Fatal("invalid or oversized RTF accepted")
		}
	}
}
