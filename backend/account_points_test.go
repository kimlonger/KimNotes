package main

import (
	"context"
	"io"
	"net/http"
	"testing"
	"time"
)

func TestCheckInRewardAndBeijingDay(t *testing.T) {
	for day, reward := range map[int]int{1: 5, 2: 5, 3: 10, 4: 5, 5: 5, 6: 10, 7: 20, 8: 5, 14: 5} {
		if got := rewardForCheckIn(day); got != reward {
			t.Fatalf("day %d earned %d instead of %d", day, got, reward)
		}
	}
	midnight := time.Date(2026, 11, 16, 16, 0, 0, 0, time.UTC)
	if accountPointsDay(midnight.Add(-time.Second)) != "2026-11-16" || accountPointsDay(midnight) != "2026-11-17" {
		t.Fatal("check-in must reset on Beijing midnight")
	}
}

func TestSharedAccountPointsAndConcurrentDownloads(t *testing.T) {
	e := guitarTestEnv(t)
	ctx := context.Background()
	notes := e.account("shared-points")
	main := e.mainLogin("shared-points")
	status, _, login := e.request("POST", "guitar/auth/login", credentials{Email: "shared-points@example.com", Password: "Test-password-123"}, nil)
	if status != 200 {
		t.Fatal("guitar login", status)
	}
	guitar := login.Cookies()[0]
	if status, _, _ := e.request("GET", "main/account/points", nil, notes); status != 401 {
		t.Fatal("notes cookie became a main session", status)
	}
	_, initial, _ := e.request("GET", "guitar/account/points", nil, guitar)
	if initial["balance"] != float64(0) || initial["checkedIn"] != false {
		t.Fatal("unexpected initial wallet", initial)
	}
	// Simulate a carried balance and six consecutive prior days in this isolated schema.
	_, err := e.db.Exec(ctx, `INSERT INTO account_point_wallets(user_id,balance,checked_in_on,streak) VALUES($1,2,$2,6)`, main.user, accountPointsDay(time.Now().Add(-24*time.Hour)))
	if err != nil {
		t.Fatal(err)
	}
	clients := map[string]*http.Cookie{"main": main.cookie, "kimnotes": notes, "guitar": guitar}
	readStatus := func(app, path string, method string, cookie *http.Cookie) int {
		req, err := http.NewRequest(method, e.server.URL+"/api/v1/apps/"+app+path, nil)
		if err != nil {
			return 0
		}
		req.Header.Set("Origin", "https://notes.test")
		req.AddCookie(cookie)
		res, err := http.DefaultClient.Do(req)
		if err != nil {
			return 0
		}
		defer res.Body.Close()
		io.Copy(io.Discard, res.Body)
		return res.StatusCode
	}
	statuses := make(chan int, len(clients))
	for app, cookie := range clients {
		go func() { statuses <- readStatus(app, "/account/points/check-in", "POST", cookie) }()
	}
	for range clients {
		if received := <-statuses; received != 200 {
			t.Fatal("cross-app check-in", received)
		}
	}
	for app, cookie := range clients {
		_, points, _ := e.request("GET", app+"/account/points", nil, cookie)
		if points["balance"] != float64(22) || points["streak"] != float64(7) || points["checkedIn"] != true {
			t.Fatalf("wallet was not shared or awarded more than once: %s %v", app, points)
		}
		expectedCost := float64(0)
		if app == guitarAppID {
			expectedCost = float64(guitarDownloadCost)
		}
		if points["downloadPoints"] != expectedCost {
			t.Fatalf("spending policy should be scoped to %s: %v", app, points)
		}
	}
	if _, err := e.db.Exec(ctx, `INSERT INTO guitar_scores(id,title,category,difficulty,credit,mime,bytes) VALUES('points-pdf','PDF 谱','弹唱','中级','test','application/pdf',$1)`, []byte("%PDF-1.4\n%%EOF")); err != nil {
		t.Fatal(err)
	}
	const path = "/scores/points-pdf"
	if s := readStatus("guitar", path+"/file", "GET", guitar); s != 200 {
		t.Fatal("reading should remain free", s)
	}
	if s := readStatus("guitar", path+"/download", "GET", guitar); s != 405 {
		t.Fatal("cross-site cookie GET can spend points", s)
	}
	_, quote, _ := e.request("GET", "guitar"+path+"/download-quote", nil, guitar)
	if quote["downloadPoints"] != float64(5) || quote["balance"] != float64(22) {
		t.Fatal("quote should not debit points and must use fixed tariff", quote)
	}
	downloads := make(chan int, 5)
	for range 5 {
		go func() { downloads <- readStatus("guitar", path+"/download", "POST", guitar) }()
	}
	results := map[int]int{}
	for range 5 {
		results[<-downloads]++
	}
	if results[200] != 4 || results[402] != 1 {
		t.Fatal("repeated downloads must each cost 5 with no daily count cap", results)
	}
	_, balance, _ := e.request("GET", "kimnotes/account/points", nil, notes)
	if balance["balance"] != float64(2) {
		t.Fatal("notes did not observe guitar debits", balance)
	}
	var entries, delta int
	if err := e.db.QueryRow(ctx, `SELECT count(*),sum(delta) FROM account_point_events WHERE user_id=$1`, main.user).Scan(&entries, &delta); err != nil || entries != 5 || delta != 0 {
		t.Fatal("ledger does not match wallet", entries, delta, err)
	}
	var downloadsRecorded int
	if err := e.db.QueryRow(ctx, `SELECT count(*) FROM account_point_events
 WHERE user_id=$1 AND app_id='guitar' AND event_kind='score_download' AND score_id='points-pdf'`,
		main.user).Scan(&downloadsRecorded); err != nil || downloadsRecorded != 4 {
		t.Fatal("debits lost their spending-policy provenance", downloadsRecorded, err)
	}
	if _, err := e.db.Exec(ctx, `UPDATE rate_limits SET count=$2 WHERE key=$1`, digest("guitar:media:user:"+main.user), guitarMediaAccountHourBytes); err != nil {
		t.Fatal(err)
	}
	if s := readStatus("guitar", path+"/file", "GET", guitar); s != 429 {
		t.Fatal("inline files bypassed the bandwidth guard", s)
	}
}
