package main

import (
	"bytes"
	"context"
	"encoding/json"
	"image"
	"image/png"
	"mime/multipart"
	"net/http"
	"testing"
)

var guitarUploadLifecycleSchema = migrationFixture("028_guitar_upload_lifecycle")

const privateUploadDescription = "同一份投稿说明，C调 95 BPM"

func privateUploadFixture(t *testing.T, e *testEnv, owner *http.Cookie, pdf bool) (string, string) {
	t.Helper()
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	for key, value := range map[string]string{"title": "仅自己可见的练习", "description": privateUploadDescription, "category": "弹唱", "difficultyStars": "2", "permission": "confirmed"} {
		if err := form.WriteField(key, value); err != nil {
			t.Fatal(err)
		}
	}
	if pdf {
		file, _ := form.CreateFormFile("file", "score.pdf")
		_, _ = file.Write([]byte("%PDF-1.4\n%%EOF"))
	} else {
		for _, name := range []string{"first.png", "second.png"} {
			file, _ := form.CreateFormFile("file", name)
			if err := png.Encode(file, image.NewRGBA(image.Rect(0, 0, 16, 24))); err != nil {
				t.Fatal(err)
			}
		}
	}
	_ = form.Close()
	req, _ := http.NewRequest("POST", e.server.URL+"/api/v1/apps/guitar/uploads", &body)
	req.Header.Set("Origin", "https://notes.test")
	req.Header.Set("Content-Type", form.FormDataContentType())
	req.AddCookie(owner)
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	var result map[string]any
	_ = json.NewDecoder(res.Body).Decode(&result)
	if res.StatusCode != 201 {
		t.Fatal("upload failed", res.StatusCode, result)
	}
	return result["id"].(string), result["previewScoreId"].(string)
}

func TestPrivateUploadPracticeAndApproval(t *testing.T) {
	e := guitarTestEnv(t)
	owner, other := guitarTestAccount(t, e, "private-owner"), guitarTestAccount(t, e, "private-other")
	id, score := privateUploadFixture(t, e, owner, false)
	if score != "upload-"+id {
		t.Fatal("private and public score IDs must match", score, id)
	}
	if s, list, _ := e.request("GET", "guitar/uploads", nil, owner); s != 200 || list["uploads"].([]any)[0].(map[string]any)["previewScoreId"] != score {
		t.Fatal("owner cannot find their upload", s, list)
	}
	if s, detail, _ := e.request("GET", "guitar/scores/"+score, nil, owner); s != 200 || detail["privateStatus"] != "pending" || len(detail["pages"].([]any)) != 2 || detail["notation"] != nil || detail["description"] != privateUploadDescription {
		t.Fatal("owner cannot view pending image score", s, detail)
	}
	var copiedDescription string
	if err := e.db.QueryRow(context.Background(), `SELECT description FROM guitar_scores WHERE id=$1`, score).Scan(&copiedDescription); err != nil || copiedDescription != "" {
		t.Fatal("upload description duplicated in score identity", copiedDescription, err)
	}
	for _, suffix := range []string{"", "/file", "/pages/2"} {
		if s, _, _ := e.request("GET", "guitar/scores/"+score+suffix, nil, owner); s != 200 {
			t.Fatal("owner cannot view score page", suffix, s)
		}
		if s, _, _ := e.request("GET", "guitar/scores/"+score+suffix, nil, other); s != 404 {
			t.Fatal("another user accessed pending upload", suffix, s)
		}
		if s, _, _ := e.request("GET", "guitar/scores/"+score+suffix, nil, nil); s != 401 {
			t.Fatal("anonymous user accessed pending upload", suffix, s)
		}
	}
	if s, catalog, _ := e.request("GET", "guitar/scores?q=仅自己可见的练习", nil, owner); s != 200 || len(catalog["scores"].([]any)) != 0 {
		t.Fatal("pending score entered public catalog", s, catalog)
	}
	if s, _, _ := e.request("POST", "guitar/practice", map[string]any{"scoreId": score}, other); s != 404 {
		t.Fatal("another user practiced private upload", s)
	}
	if s, _, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", map[string]any{"page": 2, "pageCount": 2}, other); s != 404 {
		t.Fatal("another user changed private reading progress", s)
	}
	if s, _, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", map[string]any{"page": 2, "pageCount": 2, "learned": true}, owner); s != 200 {
		t.Fatal("owner cannot save progress", s)
	}
	s, started, _ := e.request("POST", "guitar/practice", map[string]any{"scoreId": score}, owner)
	if s != 201 {
		t.Fatal("owner cannot practice private upload", s, started)
	}
	session := started["id"].(string)
	if _, err := e.db.Exec(context.Background(), `UPDATE guitar_practice_sessions SET started_at=now()-interval '70 seconds' WHERE id=$1`, session); err != nil {
		t.Fatal(err)
	}
	if s, _, _ := e.request("PUT", "guitar/practice/"+session, nil, owner); s != 200 {
		t.Fatal("could not stop private practice", s)
	}
	if s, data, _ := e.request("GET", "guitar/workbench", nil, owner); s != 200 || data["learnedCount"] != float64(1) || len(data["progress"].([]any)) != 1 {
		t.Fatal("private progress missing from owner workbench", s, data)
	} else if data["progress"].([]any)[0].(map[string]any)["key"] != "C" || data["records"].([]any)[0].(map[string]any)["bpm"] != float64(95) {
		t.Fatal("private progress lost metadata from the upload description", data)
	}
	if s, data, _ := e.request("GET", "guitar/practice", nil, owner); s != 200 || len(data["recent"].([]any)) != 1 || data["recent"].([]any)[0].(map[string]any)["available"] != true {
		t.Fatal("private practice missing", s, data)
	} else if data["recent"].([]any)[0].(map[string]any)["description"] != privateUploadDescription {
		t.Fatal("private practice lost the upload description", data)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"approve", id}); err != nil {
		t.Fatal(err)
	}
	if s, detail, _ := e.request("GET", "guitar/scores/"+score, nil, other); s != 200 || detail["privateStatus"] != nil || len(detail["pages"].([]any)) != 2 || detail["description"] != privateUploadDescription {
		t.Fatal("approved upload was not public", s, detail)
	}
	if s, data, _ := e.request("GET", "guitar/workbench", nil, owner); s != 200 || data["progress"].([]any)[0].(map[string]any)["page"] != float64(2) {
		t.Fatal("approval lost reading progress", s, data)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, owner); s != 200 {
		t.Fatal("could not remove personal upload", s)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score+"/pages/2", nil, other); s != 200 {
		t.Fatal("removing personal upload removed public score", s)
	}
}

func TestPrivatePDFRejectedAndLegacyBackfill(t *testing.T) {
	e := guitarTestEnv(t)
	owner, other := guitarTestAccount(t, e, "private-pdf-owner"), guitarTestAccount(t, e, "private-pdf-other")
	id, score := privateUploadFixture(t, e, owner, true)
	// Reconstruct an upload committed by an older process during rollout of 025.
	undoUploadLifecycleFixture(t, e)
	if _, err := e.db.Exec(context.Background(), `DELETE FROM guitar_scores WHERE id=$1`, score); err != nil {
		t.Fatal(err)
	}
	if s, list, _ := e.request("GET", "guitar/uploads", nil, owner); s != 200 || list["uploads"].([]any)[0].(map[string]any)["previewScoreId"] != nil {
		t.Fatal("upload list unexpectedly wrote a missing score identity", s, list)
	}
	if _, err := e.db.Exec(context.Background(), guitarUploadLifecycleSchema); err != nil {
		t.Fatal("legacy upload migration failed", err)
	}
	if s, list, _ := e.request("GET", "guitar/uploads", nil, owner); s != 200 || list["uploads"].([]any)[0].(map[string]any)["previewScoreId"] != score {
		t.Fatal("migration did not restore the preview identity", s, list)
	}
	if s, detail, _ := e.request("GET", "guitar/scores/"+score, nil, owner); s != 200 || detail["mime"] != "application/pdf" || detail["privateStatus"] != "pending" {
		t.Fatal("legacy PDF cannot be read", s, detail)
	}
	if s, _, res := e.request("GET", "guitar/scores/"+score+"/file", nil, owner); s != 200 || res.Header.Get("Content-Type") != "application/pdf" {
		t.Fatal("owner cannot view PDF bytes", s, res.Header)
	}
	if err := reviewGuitar(context.Background(), e.db, []string{"reject", id}); err != nil {
		t.Fatal(err)
	}
	if s, detail, _ := e.request("GET", "guitar/scores/"+score, nil, owner); s != 200 || detail["privateStatus"] != "rejected" {
		t.Fatal("owner lost rejected upload", s, detail)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score+"/file", nil, other); s != 404 {
		t.Fatal("rejected upload became public", s)
	}
	if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, owner); s != 200 {
		t.Fatal("could not remove private upload", s)
	}
	if s, _, _ := e.request("GET", "guitar/scores/"+score, nil, owner); s != 404 {
		t.Fatal("removed private upload remained accessible", s)
	}
}

func undoUploadLifecycleFixture(t *testing.T, e *testEnv) {
	t.Helper()
	if _, err := e.db.Exec(context.Background(), `DROP TRIGGER guitar_uploads_archive_withdrawn_score ON guitar_uploads;
 DROP FUNCTION archive_withdrawn_guitar_upload();
 ALTER TABLE guitar_scores DROP COLUMN withdrawn_upload_owner_id;
 UPDATE guitar_scores s SET description=u.description FROM guitar_uploads u
 WHERE s.status='private' AND s.id='upload-'||u.id::text AND s.app_id=u.app_id`); err != nil {
		t.Fatal(err)
	}
}

func TestWithdrawnGuitarUploadKeepsPracticeAndAcceptsDelayedWrites(t *testing.T) {
	for _, legacy := range []bool{false, true} {
		name := "current-delete"
		if legacy {
			name = "historical-orphan"
		}
		t.Run(name, func(t *testing.T) {
			e := guitarTestEnv(t)
			owner, other := guitarTestAccount(t, e, "withdrawn-owner"), guitarTestAccount(t, e, "withdrawn-other")
			id, score := privateUploadFixture(t, e, owner, false)
			reading := map[string]any{"page": 2, "pageCount": 2}
			if s, _, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", reading, owner); s != 200 {
				t.Fatal("failed to record reading", s)
			}
			s, started, _ := e.request("POST", "guitar/practice", map[string]any{"scoreId": score, "automatic": true}, owner)
			if s != 201 {
				t.Fatal("failed to start practice", s, started)
			}
			practice := started["id"].(string)
			if _, err := e.db.Exec(context.Background(), `UPDATE guitar_practice_sessions SET started_at=now()-interval '90 seconds' WHERE id=$1`, practice); err != nil {
				t.Fatal(err)
			}
			if legacy {
				undoUploadLifecycleFixture(t, e)
			}
			if s, _, _ := e.request("DELETE", "guitar/uploads/"+id, nil, owner); s != 200 {
				t.Fatal("failed to withdraw private upload", s)
			}
			if legacy {
				if _, err := e.db.Exec(context.Background(), guitarUploadLifecycleSchema); err != nil {
					t.Fatal("could not archive historical orphan", err)
				}
			}
			var archived bool
			if err := e.db.QueryRow(context.Background(), `SELECT s.status='archived' AND s.withdrawn_upload_owner_id=r.user_id
 FROM guitar_scores s JOIN guitar_reading_progress r ON r.app_id=s.app_id AND r.score_id=s.id WHERE s.id=$1`, score).Scan(&archived); err != nil || !archived {
				t.Fatal("stable identity or owner history lost on withdrawal", archived, err)
			}
			for _, suffix := range []string{"", "/file", "/pages/2"} {
				if s, _, _ := e.request("GET", "guitar/scores/"+score+suffix, nil, owner); s != 404 {
					t.Fatal("withdrawn score exposed via", suffix, s)
				}
			}
			if s, _, _ := e.request("POST", "guitar/practice", map[string]any{"scoreId": score}, owner); s != 404 {
				t.Fatal("new practice started for a withdrawn score", s)
			}
			if s, _, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", reading, other); s != 404 {
				t.Fatal("different user changed withdrawn progress", s)
			}
			if s, _, _ := e.request("PATCH", "guitar/scores/"+score+"/reading", map[string]any{"page": 1, "pageCount": 2, "learned": true}, owner); s != 200 {
				t.Fatal("delayed reading update was rejected", s)
			}
			if s, _, _ := e.request("PUT", "guitar/practice/"+practice, map[string]any{"activeSeconds": 80}, owner); s != 200 {
				t.Fatal("delayed practice completion was rejected", s)
			}
			var page int
			if err := e.db.QueryRow(context.Background(), `SELECT page FROM guitar_reading_progress WHERE score_id=$1`, score).Scan(&page); err != nil || page != 1 {
				t.Fatal("delayed reading update was not persisted", page, err)
			}
			if s, workbench, _ := e.request("GET", "guitar/workbench", nil, owner); s != 200 || workbench["totalSeconds"] != float64(80) {
				t.Fatal("withdrawal lost completed practice time", s, workbench)
			} else if workbench["records"].([]any)[0].(map[string]any)["bpm"] != float64(95) {
				t.Fatal("withdrawal lost historical practice metadata", workbench)
			}
		})
	}
}
