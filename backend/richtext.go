package main

import (
	"bytes"
	"compress/gzip"
	"encoding/base64"
	"io"
	"regexp"
	"strings"
	"unicode/utf8"
)

var unsafeRTF = regexp.MustCompile(`(?i)\\(?:object|objdata|objclass|filetbl|bin)(?:[^a-zA-Z]|$)`)

func validateRichText(rtf, packed string) error {
	_, e := unpackRichText(rtf, packed)
	return e
}
func unpackRichText(rtf, packed string) (string, error) {
	invalid := func() (string, error) {
		return "", fail(400, "invalid_rtf", "富文本无效、含不支持的嵌入对象，或解压后超过 32 MB。")
	}
	if packed != "" {
		if rtf != "" || len(packed) > 2<<20 {
			return invalid()
		}
		raw, e := base64.StdEncoding.DecodeString(packed)
		if e != nil {
			return invalid()
		}
		reader, e := gzip.NewReader(bytes.NewReader(raw))
		if e != nil {
			return invalid()
		}
		defer reader.Close()
		data, e := io.ReadAll(io.LimitReader(reader, (32<<20)+1))
		if e != nil || len(data) > 32<<20 {
			return invalid()
		}
		rtf = string(data)
	}
	if rtf != "" && (!strings.HasPrefix(rtf, `{\rtf`) || !utf8.ValidString(rtf) || len(rtf) > 32<<20 || unsafeRTF.MatchString(rtf)) {
		return invalid()
	}
	return rtf, nil
}
