package main

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"time"

	"github.com/jackc/pgx/v5"
)

const guitarToday = `(now() AT TIME ZONE 'Asia/Shanghai')::date`
const guitarSongPracticeTitle = "歌曲弹唱"

// All practice is recorded once. Estimates created by legacy checklist actions
// are explicit events and disappear when actual timing replaces them.
var guitarPracticeEvents = `
 SELECT p.user_id,p.id,p.score_id,p.record_kind AS source,
  CASE WHEN p.record_kind IN ('score','task') THEN p.started_at END AS at,
  COALESCE(p.practice_day,(p.started_at AT TIME ZONE 'Asia/Shanghai')::date) AS day,
  CASE WHEN p.record_kind='score' THEN COALESCE(s.title,'曲谱已下架') ELSE p.title END AS title,
  p.seconds,p.note,CASE WHEN p.record_kind='estimate' THEN 'task' ELSE p.record_kind END AS kind,
  CASE WHEN p.record_kind='score' THEN ` + guitarCatalogBPM + ` END AS bpm,
  CASE WHEN p.record_kind<>'estimate' THEN p.task_id END AS task_id,
  CASE WHEN p.record_kind='score' THEN '` + guitarSongPracticeTitle + `' END AS category
 FROM guitar_practice_sessions p LEFT JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id
 WHERE p.app_id='guitar' AND p.ended_at IS NOT NULL AND p.seconds>0`

func (a *App) guitarWorkbench(w http.ResponseWriter, r *http.Request) error {
	var out json.RawMessage
	err := a.db.QueryRow(r.Context(), `
 WITH events AS MATERIALIZED (SELECT * FROM (`+guitarPracticeEvents+`) e WHERE user_id=$1),
 days AS (SELECT day,sum(seconds) AS seconds FROM events GROUP BY day),
 today AS (SELECT `+guitarToday+` AS d),
 task_times AS (SELECT task_id,sum(seconds) AS seconds FROM events CROSS JOIN today WHERE task_id IS NOT NULL AND day=today.d GROUP BY task_id),
 checkins AS (SELECT day,row_number() OVER(ORDER BY day DESC)::int AS n FROM days WHERE seconds>=900),
 streak AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM checkins WHERE day=today.d) THEN today.d ELSE today.d-1 END AS start FROM today)
 SELECT jsonb_build_object(
 'today', today.d,
 'weeklyMinutes',COALESCE(p.weekly_minutes,150),
 'weeklyDays',COALESCE(p.weekly_days,5),
 'firstTuned',COALESCE(p.first_tuned,false),
 'equippedTitle',COALESCE(p.equipped_title,''),
 'totalSeconds',COALESCE((SELECT sum(seconds) FROM days),0),
 'checkinDays',(SELECT count(*) FROM checkins),
 'checkinStreak',(SELECT count(*) FROM checkins CROSS JOIN streak WHERE day=streak.start-(n-1)),
 'learnedCount',(SELECT count(*) FROM guitar_reading_progress r JOIN guitar_scores s ON s.app_id=r.app_id AND s.id=r.score_id
  WHERE r.app_id='guitar' AND r.user_id=$1 AND r.reading_started AND r.learned AND `+guitarScoreVisibleSQL("$1")+`),
 'days',COALESCE((SELECT jsonb_object_agg(day,seconds) FROM days WHERE day>=today.d-118),'{}'),
 'tasks',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',t.id,'title',t.title,'minutes',t.minutes,'done',t.done,'actualSeconds',COALESCE(tt.seconds,0))
  ORDER BY t.position,t.id) FROM guitar_practice_tasks t LEFT JOIN task_times tt ON tt.task_id=t.id WHERE t.user_id=$1 AND t.day=today.d AND t.deleted_at IS NULL),'[]'),
 'canCopy',EXISTS(SELECT 1 FROM guitar_practice_tasks WHERE user_id=$1 AND day=today.d-1 AND deleted_at IS NULL),
 'records',COALESCE((SELECT jsonb_agg(to_jsonb(e) - 'user_id' - 'task_id' - 'source' ORDER BY day DESC,at DESC NULLS FIRST,id)
  FROM (SELECT * FROM events ORDER BY day DESC,at DESC NULLS FIRST,id LIMIT 100) e),'[]'),
 'progress',COALESCE((SELECT jsonb_agg(jsonb_build_object(
 'id',s.id,'title',s.title,'artist',s.artist,'category',s.category,'page',r.page,
 'key',`+guitarCatalogKey+`,'capo',s.attributes->'capo',
 'difficultyStars',`+guitarStarsSQL+`,
 'pageCount',r.page_count,'learned',r.learned,'readAt',r.read_at,
 'seconds',COALESCE((SELECT sum(seconds) FROM events WHERE score_id=s.id),0))
 ORDER BY r.read_at DESC NULLS LAST,s.id)
 FROM guitar_reading_progress r JOIN guitar_scores s ON s.app_id=r.app_id AND s.id=r.score_id
 WHERE r.user_id=$1 AND r.reading_started AND `+guitarScoreVisibleSQL("$1")+`),'[]'))
 FROM today LEFT JOIN guitar_members p ON p.app_id='guitar' AND p.user_id=$1`, identity(r).UserID).Scan(&out)
	if err != nil {
		return err
	}
	var totals struct {
		TotalSeconds int64 `json:"totalSeconds"`
		CheckinDays  int   `json:"checkinDays"`
	}
	if err := json.Unmarshal(out, &totals); err != nil {
		return err
	}
	var response map[string]json.RawMessage
	if err := json.Unmarshal(out, &response); err != nil {
		return err
	}
	frame, err := json.Marshal(guitarFrameProgress(totals.TotalSeconds, totals.CheckinDays))
	if err != nil {
		return err
	}
	response["practiceFrame"] = frame
	if a.canManageGuitar(identity(r)) {
		response["canManageGuitar"] = json.RawMessage("true")
	}
	writeJSON(w, 200, response)
	return nil
}

type guitarWorkbenchChange struct {
	Action        string `json:"action"`
	TitleID       string `json:"titleId"`
	ID            string `json:"id"`
	SessionID     string `json:"sessionId"`
	Title         string `json:"title"`
	Note          string `json:"note"`
	Day           string `json:"day"`
	Minutes       int    `json:"minutes"`
	Seconds       int    `json:"seconds"`
	WeeklyMinutes int    `json:"weeklyMinutes"`
	WeeklyDays    int    `json:"weeklyDays"`
	Direction     int    `json:"direction"`
	Done          bool   `json:"done"`
}

func (a *App) guitarUpdateWorkbench(w http.ResponseWriter, r *http.Request) error {
	var in guitarWorkbenchChange
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if err := a.guitarWriteLimit(r, "workbench", 180); err != nil {
		return err
	}
	ctx, user := r.Context(), identity(r).UserID
	var err error
	switch in.Action {
	case "first-tune":
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_members(user_id,first_tuned)
 VALUES($1,true) ON CONFLICT(app_id,user_id) DO UPDATE SET first_tuned=true`, user)
	case "equip-title":
		err = a.guitarEquipTitle(ctx, user, in.TitleID)
	case "goals":
		if in.WeeklyMinutes < 100 || in.WeeklyMinutes > 600 || in.WeeklyDays < 1 || in.WeeklyDays > 7 {
			return fail(400, "invalid_goal", "请输入有效的练习目标。")
		}
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_members(user_id,weekly_minutes,weekly_days)
 VALUES($1,$2,$3) ON CONFLICT(app_id,user_id) DO UPDATE SET weekly_minutes=$2,weekly_days=$3`,
			user, in.WeeklyMinutes, in.WeeklyDays)
	case "save-log", "remove-log":
		err = a.guitarChangePracticeLog(r, in)
	case "record-task-practice":
		err = a.guitarRecordTaskPractice(r, in)
	case "add-task", "copy-tasks", "toggle-task", "move-task", "remove-task":
		err = a.guitarChangePracticeTasks(r, in)
	default:
		return fail(400, "invalid_action", "练习操作无效。")
	}
	if err != nil {
		return err
	}
	return a.guitarWorkbench(w, r)
}

func (a *App) guitarChangePracticeLog(r *http.Request, in guitarWorkbenchChange) error {
	ctx, user := r.Context(), identity(r).UserID
	if in.ID != "" && !validID(in.ID) {
		return fail(400, "invalid_record", "练习记录无效。")
	}
	if in.Action == "remove-log" {
		_, err := a.db.Exec(ctx, `DELETE FROM guitar_practice_sessions WHERE app_id='guitar' AND record_kind='manual' AND user_id=$1 AND id=$2`, user, in.ID)
		return err
	}
	title, err := guitarText(in.Title, 1, 40)
	if err != nil {
		return err
	}
	note, err := guitarText(in.Note, 0, 60)
	if err != nil {
		return err
	}
	day, err := time.Parse("2006-01-02", in.Day)
	now := time.Now().In(time.FixedZone("Shanghai", 8*60*60))
	if err != nil || day.Year() < 2000 || in.Day > now.Format("2006-01-02") || in.Minutes < 1 || in.Minutes > 600 {
		return fail(400, "invalid_record", "请检查练习日期和时长。")
	}
	if in.ID == "" {
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_practice_sessions(user_id,id,record_kind,practice_day,title,seconds,note,started_at,ended_at)
 VALUES($1,$2,'manual',$3,$4,$5*60,$6,$3::date::timestamp AT TIME ZONE 'Asia/Shanghai',$3::date::timestamp AT TIME ZONE 'Asia/Shanghai')`,
			user, newID(), day, title, in.Minutes, note)
	} else {
		_, err = a.db.Exec(ctx, `UPDATE guitar_practice_sessions SET practice_day=$3,title=$4,seconds=$5*60,note=$6,
 started_at=$3::date::timestamp AT TIME ZONE 'Asia/Shanghai',ended_at=$3::date::timestamp AT TIME ZONE 'Asia/Shanghai'
 WHERE app_id='guitar' AND record_kind='manual' AND user_id=$1 AND id=$2`,
			user, in.ID, day, title, in.Minutes, note)
	}
	return err
}

func lockGuitarPracticeTasks(ctx context.Context, tx pgx.Tx, user string) error {
	_, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext('guitar-tasks:' || $1::text))`, user)
	return err
}

func guitarExistingSongTask(ctx context.Context, tx pgx.Tx, user string) (string, error) {
	var id string
	err := tx.QueryRow(ctx, `SELECT id::text FROM guitar_practice_tasks
 WHERE user_id=$1 AND day=`+guitarToday+` AND title=$2 AND deleted_at IS NULL ORDER BY position,id LIMIT 1`, user, guitarSongPracticeTitle).Scan(&id)
	return id, err
}

// The caller holds the same lock as checklist changes. The one automatically
// created song item may be added even if the user filled the manual task limit.
func guitarEnsureSongTask(ctx context.Context, tx pgx.Tx, user string) (string, error) {
	id, err := guitarExistingSongTask(ctx, tx, user)
	if !errors.Is(err, pgx.ErrNoRows) {
		return id, err
	}
	id = newID()
	_, err = tx.Exec(ctx, `INSERT INTO guitar_practice_tasks(user_id,id,day,title,minutes,position)
 SELECT $1,$2,`+guitarToday+`,$3,30,COALESCE(max(position),0)+1 FROM guitar_practice_tasks WHERE user_id=$1 AND day=`+guitarToday+` AND deleted_at IS NULL`,
		user, id, guitarSongPracticeTitle)
	return id, err
}

func (a *App) guitarRecordTaskPractice(r *http.Request, in guitarWorkbenchChange) error {
	if !validID(in.ID) || !validID(in.SessionID) || in.Seconds < 1 || in.Seconds > 36000 {
		return fail(400, "invalid_practice", "请输入有效的练习时长。")
	}
	ctx, user := r.Context(), identity(r).UserID
	return pgx.BeginFunc(ctx, a.db, func(tx pgx.Tx) error {
		if err := lockGuitarPracticeTasks(ctx, tx, user); err != nil {
			return err
		}
		_, err := tx.Exec(ctx, `INSERT INTO guitar_practice_sessions(user_id,id,record_kind,task_id,practice_day,title,seconds,ended_at)
 SELECT user_id,$3,'task',id,day,title,$4,now() FROM guitar_practice_tasks WHERE app_id='guitar' AND user_id=$1 AND id=$2
 ON CONFLICT(app_id,user_id,record_kind,id) DO NOTHING`, user, in.ID, in.SessionID, in.Seconds)
		if err != nil {
			return err
		}
		var saved bool
		if err = tx.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM guitar_practice_sessions
 WHERE app_id='guitar' AND user_id=$1 AND record_kind='task' AND task_id=$2 AND id=$3)`, user, in.ID, in.SessionID).Scan(&saved); err != nil {
			return err
		}
		if !saved {
			return fail(404, "not_found", "清单项不存在。")
		}
		return syncGuitarTaskEstimate(ctx, tx, user, in.ID)
	})
}

// The checklist's historical planned-time contribution is written at the
// action boundary, under the same lock as actual timer submissions.
func syncGuitarTaskEstimate(ctx context.Context, tx pgx.Tx, user, taskID string) error {
	if _, err := tx.Exec(ctx, `DELETE FROM guitar_practice_sessions
 WHERE app_id='guitar' AND user_id=$1 AND record_kind='estimate' AND task_id=$2`, user, taskID); err != nil {
		return err
	}
	_, err := tx.Exec(ctx, `INSERT INTO guitar_practice_sessions(user_id,id,record_kind,task_id,practice_day,title,seconds,started_at,ended_at)
 SELECT user_id,id,'estimate',id,day,title,minutes*60,
 day::timestamp AT TIME ZONE 'Asia/Shanghai',day::timestamp AT TIME ZONE 'Asia/Shanghai'
 FROM guitar_practice_tasks t WHERE app_id='guitar' AND user_id=$1 AND id=$2
 AND deleted_at IS NULL AND done AND minutes>0
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_sessions p WHERE p.app_id=t.app_id AND p.user_id=t.user_id
  AND p.task_id=t.id AND p.record_kind IN ('score','task') AND p.ended_at IS NOT NULL AND p.seconds>0)`, user, taskID)
	return err
}

func (a *App) guitarChangePracticeTasks(r *http.Request, in guitarWorkbenchChange) error {
	ctx, user := r.Context(), identity(r).UserID
	if in.Action != "add-task" && in.Action != "copy-tasks" && !validID(in.ID) {
		return fail(400, "invalid_task", "清单项无效。")
	}
	return pgx.BeginFunc(ctx, a.db, func(tx pgx.Tx) error {
		if err := lockGuitarPracticeTasks(ctx, tx, user); err != nil {
			return err
		}
		const current = `user_id=$1 AND deleted_at IS NULL AND day=` + guitarToday
		switch in.Action {
		case "add-task":
			title, err := guitarText(in.Title, 1, 40)
			if err != nil {
				return err
			}
			if in.Minutes < 0 || in.Minutes > 600 {
				return fail(400, "invalid_duration", "请输入有效的练习时长。")
			}
			if title == guitarSongPracticeTitle {
				if _, err := guitarExistingSongTask(ctx, tx, user); !errors.Is(err, pgx.ErrNoRows) {
					return err
				}
			}
			tag, err := tx.Exec(ctx, `INSERT INTO guitar_practice_tasks(user_id,id,day,title,minutes,position)
 SELECT $1,$2,`+guitarToday+`,$3,$4,COALESCE(max(position),0)+1 FROM guitar_practice_tasks WHERE `+current+` HAVING count(*)<40`,
				user, newID(), title, in.Minutes)
			if err == nil && tag.RowsAffected() == 0 {
				return fail(400, "task_limit", "每日最多添加 40 个清单项。")
			}
			return err
		case "copy-tasks":
			_, err := tx.Exec(ctx, `INSERT INTO guitar_practice_tasks(user_id,id,day,title,minutes,position)
 SELECT $1,gen_random_uuid(),`+guitarToday+`,title,minutes,position
 FROM guitar_practice_tasks WHERE user_id=$1 AND day=`+guitarToday+`-1 AND deleted_at IS NULL
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_tasks WHERE `+current+`)`, user)
			return err
		case "toggle-task":
			_, err := tx.Exec(ctx, `UPDATE guitar_practice_tasks SET done=$3 WHERE `+current+` AND id=$2`, user, in.ID, in.Done)
			if err != nil {
				return err
			}
			return syncGuitarTaskEstimate(ctx, tx, user, in.ID)
		case "remove-task":
			_, err := tx.Exec(ctx, `UPDATE guitar_practice_tasks SET deleted_at=now() WHERE `+current+` AND id=$2`, user, in.ID)
			if err != nil {
				return err
			}
			return syncGuitarTaskEstimate(ctx, tx, user, in.ID)
		case "move-task":
			if in.Direction != -1 && in.Direction != 1 {
				return fail(400, "invalid_direction", "移动方向无效。")
			}
			_, err := tx.Exec(ctx, `WITH current AS (SELECT id,position FROM guitar_practice_tasks WHERE `+current+` AND id=$2),
 neighbor AS (SELECT t.id,t.position FROM guitar_practice_tasks t,current c
 WHERE t.user_id=$1 AND t.day=`+guitarToday+` AND t.deleted_at IS NULL AND (t.position-c.position)*$3>0 ORDER BY abs(t.position-c.position) LIMIT 1)
 UPDATE guitar_practice_tasks t SET position=CASE WHEN t.id=c.id THEN n.position ELSE c.position END
 FROM current c,neighbor n WHERE t.id IN (c.id,n.id) AND t.user_id=$1`, user, in.ID, in.Direction)
			return err
		}
		return nil
	})
}

func (a *App) guitarUpdateReading(w http.ResponseWriter, r *http.Request) error {
	var in struct {
		Page      int   `json:"page"`
		PageCount int   `json:"pageCount"`
		Learned   *bool `json:"learned"`
	}
	if err := decode(w, r, &in); err != nil {
		return err
	}
	if err := a.guitarWriteLimit(r, "reading", 240); err != nil {
		return err
	}
	if (in.Page == 0 && in.Learned == nil) || in.Page < 0 || in.PageCount < in.Page || in.PageCount > 10000 {
		return fail(400, "invalid_page", "曲谱页码无效。")
	}
	// An installed client may be replaying a reading update from before its
	// upload was withdrawn. Only the former owner can finish that sync.
	tag, err := a.db.Exec(r.Context(), `INSERT INTO guitar_reading_progress(user_id,score_id,page,page_count,read_at,learned)
 SELECT $1,s.id,GREATEST(1,$3),GREATEST(1,$4),CASE WHEN $3>0 THEN now() END,COALESCE($5,false)
 FROM guitar_scores s WHERE s.app_id='guitar' AND s.id=$2 AND (`+guitarScoreVisibleSQL("$1")+`
 OR (s.status='archived' AND s.withdrawn_upload_owner_id=$1))
 ON CONFLICT(app_id,user_id,score_id) DO UPDATE SET
 page=CASE WHEN $3>0 THEN EXCLUDED.page ELSE guitar_reading_progress.page END,
 page_count=CASE WHEN $3>0 THEN EXCLUDED.page_count ELSE guitar_reading_progress.page_count END,
 read_at=COALESCE(EXCLUDED.read_at,guitar_reading_progress.read_at),reading_started=true,
 learned=COALESCE($5,guitar_reading_progress.learned)`, identity(r).UserID, r.PathValue("score"), in.Page, in.PageCount, in.Learned)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fail(404, "not_found", "曲谱不存在。")
	}
	writeJSON(w, 200, map[string]any{"saved": true})
	return nil
}
