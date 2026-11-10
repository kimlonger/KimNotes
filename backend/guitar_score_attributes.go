package main

import (
	"net/http"
	"regexp"
	"strconv"
	"strings"
)

type guitarScoreAttributes struct {
	Key    string   `json:"key,omitempty"`
	Capo   *int     `json:"capo,omitempty"`
	Meter  []int    `json:"meter,omitempty"`
	BPM    *int     `json:"bpm,omitempty"`
	Chords []string `json:"chords,omitempty"`
}

var guitarNoteSymbols = strings.NewReplacer("♯", "#", "♭", "b", "／", "/")
var guitarKeyPattern = regexp.MustCompile(`^[A-G][#b]?m?$`)
var guitarChordPattern = regexp.MustCompile(`^[A-G][#b]{0,2}(m|7|m7|maj7|sus2|sus4|6|m6|dim|aug|add9|5)?(/[A-G][#b]{0,2})?$`)

func guitarUploadAttributes(r *http.Request) (guitarScoreAttributes, error) {
	var out guitarScoreAttributes
	out.Key = guitarNoteSymbols.Replace(strings.TrimSpace(r.FormValue("key")))
	if out.Key != "" && !guitarKeyPattern.MatchString(out.Key) {
		return out, fail(400, "invalid_score_key", "调式格式无效。")
	}
	if value := strings.TrimSpace(r.FormValue("capo")); value != "" {
		capo, err := strconv.Atoi(value)
		if err != nil || capo < 0 || capo > 12 {
			return out, fail(400, "invalid_score_capo", "变调夹品位应为 0 到 12。")
		}
		out.Capo = &capo
	}
	if value := strings.TrimSpace(r.FormValue("meter")); value != "" {
		first, second, ok := strings.Cut(value, "/")
		beats, err1 := strconv.Atoi(first)
		unit, err2 := strconv.Atoi(second)
		if !ok || err1 != nil || err2 != nil || beats < 2 || beats > 12 || (unit != 2 && unit != 4 && unit != 8 && unit != 16) {
			return out, fail(400, "invalid_score_meter", "拍号格式无效。")
		}
		out.Meter = []int{beats, unit}
	}
	if value := strings.TrimSpace(r.FormValue("bpm")); value != "" {
		bpm, err := strconv.Atoi(value)
		if err != nil || bpm < 20 || bpm > 300 {
			return out, fail(400, "invalid_score_bpm", "每分钟拍数应在 20 到 300 之间。")
		}
		out.BPM = &bpm
	}
	chords := r.FormValue("chords")
	if len(chords) > 600 {
		return out, fail(400, "invalid_score_chords", "和弦最多填 24 个。")
	}
	seen := make(map[string]bool)
	for _, raw := range strings.Fields(strings.NewReplacer(",", " ", "，", " ", "、", " ").Replace(chords)) {
		name := guitarNoteSymbols.Replace(raw)
		if !guitarChordPattern.MatchString(name) {
			return out, fail(400, "invalid_score_chords", "请填写常用和弦名，用空格或逗号隔开。")
		}
		if !seen[name] {
			out.Chords = append(out.Chords, name)
			seen[name] = true
		}
	}
	if len(out.Chords) > 24 {
		return out, fail(400, "invalid_score_chords", "和弦最多填 24 个。")
	}
	return out, nil
}
