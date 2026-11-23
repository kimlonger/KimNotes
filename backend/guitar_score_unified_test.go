package main

import (
	"context"
	"encoding/json"
	"net/http"
	"testing"
	"time"
)

func TestUnifiedFavoriteAndReadingStateStayIndependent(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "unified-favorite-owner")
	other := guitarTestAccount(t, e, "unified-favorite-other")
	const score = "course-little-bee"
	favorite := "guitar/favorites/score/" + score
	for i := 0; i < 2; i++ {
		if status, out, _ := e.request("PUT", favorite, nil, owner); status != 200 {
			t.Fatal(status, out)
		}
	}
	_, saved, _ := e.request("GET", "guitar/favorites", nil, owner)
	items := saved["favorites"].([]any)
	if len(items) != 1 {
		t.Fatal("favorite retry duplicated state", saved)
	}
	created := items[0].(map[string]any)["createdAt"]
	_, workbench, _ := e.request("GET", "guitar/workbench", nil, owner)
	if len(workbench["progress"].([]any)) != 0 {
		t.Fatal("a favorite appeared as reading history", workbench)
	}
	if status, out, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", map[string]any{"page": 2, "pageCount": 3, "learned": true}, owner); status != 200 {
		t.Fatal(status, out)
	}
	if status, out, _ := e.request("PUT", favorite, nil, owner); status != 200 {
		t.Fatal(status, out)
	}
	_, saved, _ = e.request("GET", "guitar/favorites", nil, owner)
	if saved["favorites"].([]any)[0].(map[string]any)["createdAt"] != created {
		t.Fatal("favorite retry changed the original time", saved)
	}
	if status, out, _ := e.request("DELETE", favorite, nil, owner); status != 200 {
		t.Fatal(status, out)
	}
	_, saved, _ = e.request("GET", "guitar/favorites", nil, owner)
	_, workbench, _ = e.request("GET", "guitar/workbench", nil, owner)
	progress := workbench["progress"].([]any)
	if len(saved["favorites"].([]any)) != 0 || len(progress) != 1 {
		t.Fatal("unfavoriting removed the reading state", saved, workbench)
	}
	record := progress[0].(map[string]any)
	if record["page"] != float64(2) || record["pageCount"] != float64(3) || record["learned"] != true {
		t.Fatal("reading progress changed on unfavorite", record)
	}
	_, outsider, _ := e.request("GET", "guitar/workbench", nil, other)
	if len(outsider["progress"].([]any)) != 0 {
		t.Fatal("reading state leaked between members", outsider)
	}
}

func TestUnifiedScoreMigrationPreservesFavoritesPagesAndCanonicalNotes(t *testing.T) {
	e := guitarTestEnvBeforeCourseArchive(t)
	owner := guitarTestAccount(t, e, "unified-migration-owner")
	_, account, _ := e.request("GET", "guitar/auth/me", nil, owner)
	user := account["user"].(map[string]any)["id"].(string)
	ctx := context.Background()
	favoriteAt := time.Date(2026, 9, 1, 3, 4, 5, 0, time.UTC)
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_reading_progress(user_id,score_id,page,page_count,read_at,learned)
VALUES($1,'course-little-bee',2,3,now(),true)`, user); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_favorites(user_id,kind,target_id,created_at)
VALUES($1,'score','course-little-bee',$2),($1,'score','single-string',$2),($1,'course','retired-course',$2)`, user, favoriteAt); err != nil {
		t.Fatal(err)
	}
	upload := newID()
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_scores(id,title,category,difficulty,mime,bytes,credit)
VALUES('unified-public','published copy','弹唱','初级','image/png','public-first','test')`); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_uploads(id,user_id,title,category,difficulty,filename,mime,bytes,status,published_score_id)
VALUES($1,$2,'original upload','弹唱','初级','first.png','image/png','original-first','approved','unified-public')`, upload, user); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_upload_pages(upload_id,page,mime,bytes) VALUES($1,2,'image/png','original-second')`, upload); err != nil {
		t.Fatal(err)
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_score_pages(score_id,page,mime,bytes) VALUES('unified-public',2,'image/png','published-second')`); err != nil {
		t.Fatal(err)
	}
	request := newID()
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_messages(id,nickname,requested_title,body)
VALUES($1,'owner','旧版求谱','求谱：《旧版求谱》 原歌手' || chr(10) || '类型：指弹' || chr(10) || '保留说明')`, request); err != nil {
		t.Fatal(err)
	}
	archive := archiveGuitarCourseFixtures(t, e)
	if _, err := e.db.Exec(ctx, guitarRequestCategorySchema+"\n"+migrationFixture("031_guitar_score_unified")); err != nil {
		t.Fatal(err)
	}
	var retired int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FROM information_schema.tables
 WHERE table_schema=current_schema() AND table_name IN ('guitar_favorites','guitar_upload_pages')`).Scan(&retired); err != nil || retired != 0 {
		t.Fatal("retired business tables remain", retired, err)
	}
	var started, learned bool
	var gotAt time.Time
	var page int
	if err := e.db.QueryRow(ctx, `SELECT favorite_at,reading_started,page,learned FROM guitar_reading_progress
 WHERE user_id=$1 AND score_id='course-little-bee'`, user).Scan(&gotAt, &started, &page, &learned); err != nil || !gotAt.Equal(favoriteAt) || !started || page != 2 || !learned {
		t.Fatal("favorite migration overwrote reading progress", gotAt, started, page, learned, err)
	}
	if err := e.db.QueryRow(ctx, `SELECT reading_started FROM guitar_reading_progress
 WHERE user_id=$1 AND score_id='single-string'`, user).Scan(&started); err != nil || started {
		t.Fatal("favorite-only state gained reading history", started, err)
	}
	if err := e.db.QueryRow(ctx, "SELECT count(*) FROM "+archive+".guitar_legacy_favorites WHERE user_id=$1 AND kind='course'", user).Scan(&retired); err != nil || retired != 1 {
		t.Fatal("retired course favorite was lost", retired, err)
	}
	var original, published []byte
	if err := e.db.QueryRow(ctx, `SELECT bytes FROM guitar_score_pages WHERE upload_id=$1 AND page=2`, upload).Scan(&original); err != nil {
		t.Fatal(err)
	}
	if err := e.db.QueryRow(ctx, `SELECT bytes FROM guitar_score_pages WHERE score_id='unified-public' AND page=2`).Scan(&published); err != nil || string(original) != "original-second" || string(published) != "published-second" {
		t.Fatal("independent page originals were combined", string(original), string(published), err)
	}
	var category, note string
	if err := e.db.QueryRow(ctx, `SELECT requested_category,body FROM guitar_messages WHERE id=$1`, request).Scan(&category, &note); err != nil || category != "指弹" || note != "保留说明" {
		t.Fatal("legacy request metadata changed", category, note, err)
	}
	applyGuitarSchemaNext(t, e)
	if err := e.db.QueryRow(ctx, `SELECT bytes FROM guitar_score_files WHERE original_upload_id=$1 AND file_no=2`, upload).Scan(&original); err != nil || string(original) != "original-second" {
		t.Fatal("a distinct original was lost in the file migration", string(original), err)
	}
	var originalTitle string
	if err := e.db.QueryRow(ctx, `SELECT original_metadata->>'title' FROM guitar_uploads WHERE id=$1`, upload).Scan(&originalTitle); err != nil || originalTitle != "original upload" {
		t.Fatal("edited public metadata replaced the original", originalTitle, err)
	}
	if _, err := e.db.Exec(ctx, `DELETE FROM guitar_uploads WHERE id=$1`, upload); err != nil {
		t.Fatal(err)
	}
	if err := e.db.QueryRow(ctx, `SELECT count(*) FROM guitar_score_files WHERE original_upload_id=$1`, upload).Scan(&retired); err != nil || retired != 0 {
		t.Fatal("original pages survived their deleted upload", retired, err)
	}
	if err := e.db.QueryRow(ctx, `SELECT bytes FROM guitar_score_files WHERE score_id='unified-public' AND file_no=2`).Scan(&published); err != nil || string(published) != "published-second" {
		t.Fatal("deleting an upload removed a published arrangement", string(published), err)
	}
}

func TestUnifiedPageOwnersAndPrivatePublication(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "unified-pages-owner")
	other := guitarTestAccount(t, e, "unified-pages-other")
	upload, score := privateUploadFixture(t, e, owner, false)
	ctx := context.Background()
	for _, invalid := range []struct {
		query string
		args  []any
	}{
		{`INSERT INTO guitar_score_files(file_no,mime,bytes) VALUES(2,'image/png','bad')`, nil},
		{`INSERT INTO guitar_score_files(score_id,original_upload_id,file_no,mime,bytes) VALUES('single-string',$1,3,'image/png','bad')`, []any{upload}},
		{`INSERT INTO guitar_score_files(score_id,file_no,mime,bytes) VALUES($1,3,'application/pdf','bad')`, []any{score}},
	} {
		if _, err := e.db.Exec(ctx, invalid.query, invalid.args...); err == nil {
			t.Fatal("an invalid page owner passed database constraints", invalid.query)
		}
	}
	if status, _, _ := e.request("GET", "guitar/scores/"+score+"/pages/2", nil, other); status != http.StatusNotFound {
		t.Fatal("another member read a private upload page", status)
	}
	if err := reviewGuitar(ctx, e.db, []string{"approve", upload}); err != nil {
		t.Fatal(err)
	}
	var originals, public int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FILTER(WHERE original_upload_id=$1),count(*) FILTER(WHERE score_id=$2)
 FROM guitar_score_files`, upload, score).Scan(&originals, &public); err != nil || originals != 0 || public != 2 {
		t.Fatal("publication copied the file set or lost a page", originals, public, err)
	}
	if status, out, _ := e.request("DELETE", "guitar/uploads/"+upload, nil, owner); status != 200 {
		t.Fatal(status, out)
	}
	if status, _, _ := e.request("GET", "guitar/scores/"+score+"/pages/2", nil, other); status != 200 {
		t.Fatal("removing approved originals removed public pages", status)
	}
}

func TestCanonicalScoreMetadataAgreesAcrossCatalogAndDetail(t *testing.T) {
	e := guitarTestEnv(t)
	owner := guitarTestAccount(t, e, "canonical-metadata-owner")
	ctx := context.Background()
	const notation = `{"key":"D","bpm":120,"capo":2,"meter":[4,4],"chords":["D","G"]}`
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_scores(id,title,description,category,difficulty,notation,credit,attributes)
 VALUES('canonical-metadata','canonical metadata test','C调 77 BPM 变调夹3 6/8','弹唱','初级',$1,'original source','{"key":"F","bpm":101}')`, json.RawMessage(notation)); err != nil {
		t.Fatal(err)
	}
	var original string
	var normalized guitarScoreAttributes
	var stars float64
	if err := e.db.QueryRow(ctx, `SELECT notation::text,attributes,difficulty_stars FROM guitar_scores WHERE id='canonical-metadata'`).Scan(&original, &normalized, &stars); err != nil {
		t.Fatal(err)
	}
	var left, right any
	_ = json.Unmarshal([]byte(original), &left)
	_ = json.Unmarshal([]byte(notation), &right)
	originalJSON, _ := json.Marshal(left)
	wantJSON, _ := json.Marshal(right)
	if string(originalJSON) != string(wantJSON) || normalized.Key != "F" || normalized.BPM == nil || *normalized.BPM != 101 || normalized.Capo == nil || *normalized.Capo != 2 || stars != 2 {
		t.Fatal("metadata precedence changed the source notation", original, normalized, stars)
	}
	status, list, _ := e.request("GET", "guitar/scores?q=canonical&key=F", nil, nil)
	if status != 200 || len(list["scores"].([]any)) != 1 {
		t.Fatal("catalog filtering disagreed with canonical metadata", status, list)
	}
	row := list["scores"].([]any)[0].(map[string]any)
	status, detail, _ := e.request("GET", "guitar/scores/canonical-metadata", nil, owner)
	if status != 200 || row["key"] != detail["key"] || row["bpm"] != detail["bpm"] || row["capo"] != detail["capo"] {
		t.Fatal("catalog and reader returned different metadata", row, detail)
	}
	if _, err := e.db.Exec(ctx, `UPDATE guitar_scores SET notation=jsonb_set(jsonb_set(notation,'{key}','"G"'),'{capo}','4') WHERE id='canonical-metadata'`); err != nil {
		t.Fatal(err)
	}
	if err := e.db.QueryRow(ctx, `SELECT attributes FROM guitar_scores WHERE id='canonical-metadata'`).Scan(&normalized); err != nil || normalized.Key != "F" || normalized.BPM == nil || *normalized.BPM != 101 || normalized.Capo == nil || *normalized.Capo != 4 {
		t.Fatal("legacy notation updates overwrote explicit metadata or left inherited values stale", normalized, err)
	}
}
