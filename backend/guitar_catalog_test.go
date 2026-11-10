package main

import (
	"context"
	"net/url"
	"strings"
	"testing"
)

func TestGuitarCatalogTitleSearchPaginationAndMetadata(t *testing.T) {
	e := guitarTestEnv(t)
	cookie := guitarTestAccount(t, e, "catalog-reader")
	for _, fixture := range []struct{ id, title string }{{"catalog-literal", "测试100%_稿"}, {"catalog-neighbor", "测试100xx稿"}} {
		_, err := e.db.Exec(context.Background(), `INSERT INTO guitar_scores(id,title,artist,genre,description,category,difficulty,notation,credit)
  SELECT $1,$2,'只在歌手中出现','民谣','只在描述中出现','弹唱','初级',notation,'test' FROM guitar_scores WHERE id='course-little-bee'`, fixture.id, fixture.title)
		if err != nil {
			t.Fatal(err)
		}
	}
	search := func(query string) map[string]any {
		t.Helper()
		status, out, _ := e.request("GET", "guitar/scores?q="+url.QueryEscape(query), nil, cookie)
		if status != 200 {
			t.Fatal(status, out)
		}
		return out
	}
	for _, query := range []string{"只在歌手中出现", "只在描述中出现", `%' OR 1=1 --`} {
		if len(search(query)["scores"].([]any)) != 0 {
			t.Fatal("search matched non-title data or SQL syntax", query)
		}
	}
	literal := search("100%_")
	if len(literal["scores"].([]any)) != 1 || literal["scores"].([]any)[0].(map[string]any)["id"] != "catalog-literal" {
		t.Fatal("wildcards were not literal", literal)
	}
	_, filtered, _ := e.request("GET", "guitar/scores?category="+url.QueryEscape("弹唱")+"&difficulty="+url.QueryEscape("初级")+"&pageSize=1", nil, cookie)
	row := filtered["scores"].([]any)[0].(map[string]any)
	if filtered["pagination"].(map[string]any)["total"] != float64(2) || row["artist"] != "只在歌手中出现" || row["genre"] != "民谣" {
		t.Fatal("filters or metadata", filtered)
	}
	for _, field := range []string{"notation", "preview", "bytes"} {
		if _, ok := row[field]; ok {
			t.Fatal("library leaked unnecessary score data", field)
		}
	}
	_, second, _ := e.request("GET", "guitar/scores?category="+url.QueryEscape("弹唱")+"&difficulty="+url.QueryEscape("初级")+"&pageSize=1&page=2", nil, cookie)
	if row["id"] == second["scores"].([]any)[0].(map[string]any)["id"] {
		t.Fatal("pagination repeated a score")
	}
	for _, query := range []string{"page=0", "pageSize=51", "pageSize=oops", "category=unknown", "difficulty=unknown"} {
		if status, _, _ := e.request("GET", "guitar/scores?"+query, nil, cookie); status != 400 {
			t.Fatal("invalid filter accepted", query, status)
		}
	}
}

func TestGuitarScoreMetadataValidation(t *testing.T) {
	name, genre, err := guitarScoreMetadata("  歌手甲  ", " 民谣 ")
	if err != nil || name != "歌手甲" || genre != "民谣" {
		t.Fatal(name, genre, err)
	}
	if _, _, err = guitarScoreMetadata(strings.Repeat("字", 121), ""); err == nil {
		t.Fatal("oversized artist accepted")
	}
	if _, _, err = guitarScoreMetadata("", "arbitrary"); err == nil {
		t.Fatal("invalid genre accepted")
	}
}
