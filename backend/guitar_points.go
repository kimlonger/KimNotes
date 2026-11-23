package main

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/jackc/pgx/v5"
)

const (
	accountBaseCheckInReward    = 5
	guitarDownloadCost          = 5
	guitarMaxDownloadBytes      = 50 << 20
	guitarMediaAccountHourBytes = 128 << 20
	guitarMediaIPHourBytes      = 512 << 20
)

var accountCheckInRewards = [7]int{accountBaseCheckInReward, accountBaseCheckInReward, 10,
	accountBaseCheckInReward, accountBaseCheckInReward, 10, 20}

type pointSpendingPolicy struct {
	ScoreDownload int
}

// Balance and check-in are shared. A spending action belongs to the app that
// provides it. Main and Notes currently have no paid actions.
var pointSpendingByApp = map[string]pointSpendingPolicy{
	"main":      {},
	"kimnotes":  {},
	guitarAppID: {ScoreDownload: guitarDownloadCost},
}

type accountPointsView struct {
	Day            string `json:"day"`
	Balance        int    `json:"balance"`
	CheckedIn      bool   `json:"checkedIn"`
	Streak         int    `json:"streak"`
	BaseReward     int    `json:"baseReward"`
	CheckInReward  int    `json:"checkInReward"`
	CheckInRewards [7]int `json:"checkInRewards"`
	DownloadPoints int    `json:"downloadPoints"`
	Awarded        int    `json:"awarded"`
}

type guitarPointsReader interface {
	QueryRow(context.Context, string, ...any) pgx.Row
}

func accountPointsDay(now time.Time) string {
	return now.In(time.FixedZone("Asia/Shanghai", 8*60*60)).Format(time.DateOnly)
}

func rewardForCheckIn(streak int) int {
	if streak >= 1 && streak <= len(accountCheckInRewards) {
		return accountCheckInRewards[streak-1]
	}
	return accountBaseCheckInReward
}

func sharedPoints(handler func(http.ResponseWriter, *http.Request) error) func(http.ResponseWriter, *http.Request) error {
	return func(w http.ResponseWriter, r *http.Request) error {
		if _, ok := pointSpendingByApp[identity(r).AppID]; !ok {
			return fail(404, "not_found", "积分功能不可用。")
		}
		return handler(w, r)
	}
}

func accountPointsState(ctx context.Context, db guitarPointsReader, app, user string, now time.Time) (accountPointsView, error) {
	v := accountPointsView{Day: accountPointsDay(now), BaseReward: accountBaseCheckInReward,
		CheckInRewards: accountCheckInRewards, DownloadPoints: pointSpendingByApp[app].ScoreDownload}
	err := db.QueryRow(ctx, `SELECT COALESCE(w.balance,0), COALESCE(w.checked_in_on=$2::date,false),
 CASE WHEN w.checked_in_on IN ($2::date,$3::date) THEN w.streak ELSE 0 END
 FROM (SELECT $1::uuid AS user_id) u LEFT JOIN account_point_wallets w USING(user_id)`, user, v.Day, accountPointsDay(now.Add(-24*time.Hour))).
		Scan(&v.Balance, &v.CheckedIn, &v.Streak)
	day := v.Streak
	if !v.CheckedIn {
		day++
	}
	v.CheckInReward = rewardForCheckIn(day)
	return v, err
}

// Lock only the shared wallet for this user. Check-ins and exports
// on other devices must observe the balance after the preceding write.
func lockAccountPoints(ctx context.Context, tx pgx.Tx, user string) error {
	if _, err := tx.Exec(ctx, `INSERT INTO account_point_wallets(user_id) VALUES($1) ON CONFLICT DO NOTHING`, user); err != nil {
		return err
	}
	var id string
	return tx.QueryRow(ctx, `SELECT user_id::text FROM account_point_wallets WHERE user_id=$1 FOR UPDATE`, user).Scan(&id)
}

func (a *App) getAccountPoints(w http.ResponseWriter, r *http.Request) error {
	p := identity(r)
	v, err := accountPointsState(r.Context(), a.db, p.AppID, p.UserID, time.Now())
	if err == nil {
		writeJSON(w, http.StatusOK, v)
	}
	return err
}

func (a *App) checkInAccountPoints(w http.ResponseWriter, r *http.Request) error {
	ctx, p := r.Context(), identity(r)
	if err := a.limited(r, "account:point-check-in:"+p.UserID, 30, time.Minute); err != nil {
		return err
	}
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if err := lockAccountPoints(ctx, tx, p.UserID); err != nil {
		return err
	}
	now := time.Now()
	v, err := accountPointsState(ctx, tx, p.AppID, p.UserID, now)
	if err != nil {
		return err
	}
	if !v.CheckedIn {
		v.Streak++
		if _, err = tx.Exec(ctx, `INSERT INTO account_point_events(user_id,day,app_id,event_kind,delta)
 VALUES($1,$2,$3,'check_in',$4)`, p.UserID, v.Day, p.AppID, v.CheckInReward); err != nil {
			return err
		}
		if _, err = tx.Exec(ctx, `UPDATE account_point_wallets SET balance=balance+$2,checked_in_on=$3,streak=$4 WHERE user_id=$1`, p.UserID, v.CheckInReward, v.Day, v.Streak); err != nil {
			return err
		}
		v.Balance += v.CheckInReward
		v.Awarded, v.CheckedIn = v.CheckInReward, true
	}
	if err = tx.Commit(ctx); err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, v)
	return nil
}

type guitarDownloadQuote struct {
	accountPointsView
	ScoreID string `json:"scoreId"`
	Size    int64  `json:"size"`
}

func guitarDownloadDetails(ctx context.Context, db guitarPointsReader, v accountPointsView, score string) (guitarDownloadQuote, error) {
	q := guitarDownloadQuote{accountPointsView: v, ScoreID: score}
	err := db.QueryRow(ctx, `SELECT octet_length(s.bytes)::bigint+COALESCE((SELECT sum(octet_length(p.bytes))
	 FROM guitar_score_pages p WHERE p.app_id=s.app_id AND p.score_id=s.id),0)
	 FROM guitar_scores s WHERE s.app_id='guitar' AND s.id=$1 AND s.status='published' AND s.bytes IS NOT NULL`, score).
		Scan(&q.Size)
	if err != nil {
		return q, err
	}
	return q, nil
}

func (a *App) guitarScoreDownloadQuote(w http.ResponseWriter, r *http.Request) error {
	ctx, user := r.Context(), identity(r).UserID
	v, err := accountPointsState(ctx, a.db, guitarAppID, user, time.Now())
	if err != nil {
		return err
	}
	q, err := guitarDownloadDetails(ctx, a.db, v, r.PathValue("score"))
	if err == nil {
		writeJSON(w, http.StatusOK, q)
	}
	return err
}

func (q guitarDownloadQuote) allow() error {
	if q.Size > guitarMaxDownloadBytes {
		return fail(http.StatusUnprocessableEntity, "score_download_too_large", "这首曲谱文件超过下载大小限制，请在看谱页浏览。")
	}
	if q.Balance < q.DownloadPoints {
		return fail(402, "insufficient_points", fmt.Sprintf("下载需要 %d 积分，当前有 %d 积分。可到「我的」签到。", q.DownloadPoints, q.Balance))
	}
	return nil
}

func recordGuitarDownload(ctx context.Context, tx pgx.Tx, user string, q guitarDownloadQuote) error {
	if _, err := tx.Exec(ctx, `INSERT INTO account_point_events(user_id,day,app_id,event_kind,score_id,delta)
 VALUES($1,$2,'guitar','score_download',$3,$4)`, user, q.Day, q.ScoreID, -q.DownloadPoints); err != nil {
		return err
	}
	_, err := tx.Exec(ctx, `UPDATE account_point_wallets SET balance=balance-$2 WHERE user_id=$1`, user, q.DownloadPoints)
	return err
}

// Reading and exporting draw from the same transfer budget. This covers the
// individual page and PDF URLs used by both current and installed readers.
func allowGuitarMedia(ctx context.Context, tx pgx.Tx, user, ip string, size int) error {
	for _, bucket := range []struct {
		key string
		max int
	}{
		{"guitar:media:user:" + user, guitarMediaAccountHourBytes},
		{"guitar:media:ip:" + ip, guitarMediaIPHourBytes},
	} {
		if size > bucket.max {
			return fail(429, "score_transfer_limit", "谱面文件超过当前传输限额。")
		}
		var used int
		err := tx.QueryRow(ctx, `INSERT INTO rate_limits(key,window_start,count) VALUES($1,now(),$2)
 ON CONFLICT(key) DO UPDATE SET count=CASE WHEN rate_limits.window_start<=now()-interval '1 hour' THEN $2 ELSE rate_limits.count+$2 END,
 window_start=CASE WHEN rate_limits.window_start<=now()-interval '1 hour' THEN now() ELSE rate_limits.window_start END
 WHERE rate_limits.window_start<=now()-interval '1 hour' OR rate_limits.count<=$3-$2 RETURNING count`, digest(bucket.key), size, bucket.max).Scan(&used)
		if err == pgx.ErrNoRows {
			return fail(429, "score_transfer_limit", "本小时谱面传输已达限额，请稍后再试。")
		}
		if err != nil {
			return err
		}
	}
	return nil
}

func (a *App) guitarReaderBudget(r *http.Request, size int) error {
	if r.Method == http.MethodHead {
		return nil
	}
	ctx := r.Context()
	tx, err := a.db.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	if err := allowGuitarMedia(ctx, tx, identity(r).UserID, guitarClientIP(r), size); err != nil {
		return err
	}
	return tx.Commit(ctx)
}
