package main

import (
	"encoding/json"
	"net/http"
	"time"

	"github.com/jackc/pgx/v5"
)

const guitarToday = `(now() AT TIME ZONE 'Asia/Shanghai')::date`

// Each completed task contributes its planned minutes once. Unchecking it removes
// that contribution. Timed score sessions and separately logged practice stay distinct.
const guitarPracticeEvents = `
 SELECT p.user_id,p.id::text,p.score_id,p.started_at AS at,
  (p.started_at AT TIME ZONE 'Asia/Shanghai')::date AS day,
  COALESCE(s.title,'曲谱已下架') AS title,p.seconds,''::text AS note,'score' AS kind,` + guitarCatalogBPM + ` AS bpm
 FROM guitar_practice_sessions p LEFT JOIN guitar_scores s ON s.app_id=p.app_id AND s.id=p.score_id
 WHERE p.app_id='guitar' AND p.ended_at IS NOT NULL AND p.seconds>0
 UNION ALL
 SELECT user_id,id::text,NULL,NULL,day,title,minutes*60,note,'manual',NULL FROM guitar_practice_logs
 UNION ALL
 SELECT user_id,id::text,NULL,NULL,day,title,minutes*60,'','task',NULL FROM guitar_practice_tasks WHERE done AND minutes>0`

func (a *App) guitarWorkbench(w http.ResponseWriter, r *http.Request) error {
	var out json.RawMessage
	err := a.db.QueryRow(r.Context(), `
 WITH events AS MATERIALIZED (SELECT * FROM (`+guitarPracticeEvents+`) e WHERE user_id=$1),
 days AS (SELECT day,sum(seconds) AS seconds FROM events GROUP BY day),
 today AS (SELECT `+guitarToday+` AS d),
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
 'days',COALESCE((SELECT jsonb_object_agg(day,seconds) FROM days WHERE day>=today.d-118),'{}'),
 'tasks',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',id,'title',title,'minutes',minutes,'done',done)
  ORDER BY position,id) FROM guitar_practice_tasks WHERE user_id=$1 AND day=today.d),'[]'),
 'canCopy',EXISTS(SELECT 1 FROM guitar_practice_tasks WHERE user_id=$1 AND day=today.d-1),
 'records',COALESCE((SELECT jsonb_agg(to_jsonb(e) - 'user_id' ORDER BY day DESC,at DESC NULLS FIRST,id)
  FROM (SELECT * FROM events ORDER BY day DESC,at DESC NULLS FIRST,id LIMIT 100) e),'[]'),
 'progress',COALESCE((SELECT jsonb_agg(jsonb_build_object(
 'id',s.id,'title',s.title,'artist',s.artist,'category',s.category,'page',r.page,
 'key',`+guitarCatalogKey+`,'capo',COALESCE(s.attributes->'capo',s.notation->'capo'),
 'difficultyStars',`+guitarStarsSQL+`,
 'pageCount',r.page_count,'learned',r.learned,'readAt',r.read_at,
 'seconds',COALESCE((SELECT sum(seconds) FROM events WHERE score_id=s.id),0))
 ORDER BY r.read_at DESC NULLS LAST,s.id)
 FROM guitar_reading_progress r JOIN guitar_scores s ON s.app_id=r.app_id AND s.id=r.score_id
 WHERE r.user_id=$1 AND s.status='published'),'[]'))
 FROM today LEFT JOIN guitar_practice_preferences p ON p.app_id='guitar' AND p.user_id=$1`, identity(r).UserID).Scan(&out)
	if err != nil {
		return err
	}
	writeJSON(w, 200, out)
	return nil
}

type guitarWorkbenchChange struct {
	Action        string `json:"action"`
	TitleID       string `json:"titleId"`
	ID            string `json:"id"`
	Title         string `json:"title"`
	Note          string `json:"note"`
	Day           string `json:"day"`
	Minutes       int    `json:"minutes"`
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
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_practice_preferences(user_id,first_tuned)
 VALUES($1,true) ON CONFLICT(app_id,user_id) DO UPDATE SET first_tuned=true`, user)
	case "equip-title":
		err = a.guitarEquipTitle(ctx, user, in.TitleID)
	case "goals":
		if in.WeeklyMinutes < 100 || in.WeeklyMinutes > 600 || in.WeeklyDays < 1 || in.WeeklyDays > 7 {
			return fail(400, "invalid_goal", "请输入有效的练习目标。")
		}
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_practice_preferences(user_id,weekly_minutes,weekly_days)
 VALUES($1,$2,$3) ON CONFLICT(app_id,user_id) DO UPDATE SET weekly_minutes=$2,weekly_days=$3`,
			user, in.WeeklyMinutes, in.WeeklyDays)
	case "save-log", "remove-log":
		err = a.guitarChangePracticeLog(r, in)
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
		_, err := a.db.Exec(ctx, `DELETE FROM guitar_practice_logs WHERE user_id=$1 AND id=$2`, user, in.ID)
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
		_, err = a.db.Exec(ctx, `INSERT INTO guitar_practice_logs(user_id,id,day,title,minutes,note) VALUES($1,$2,$3,$4,$5,$6)`,
			user, newID(), day, title, in.Minutes, note)
	} else {
		_, err = a.db.Exec(ctx, `UPDATE guitar_practice_logs SET day=$3,title=$4,minutes=$5,note=$6 WHERE user_id=$1 AND id=$2`,
			user, in.ID, day, title, in.Minutes, note)
	}
	return err
}

func (a *App) guitarChangePracticeTasks(r *http.Request, in guitarWorkbenchChange) error {
	ctx, user := r.Context(), identity(r).UserID
	if in.Action != "add-task" && in.Action != "copy-tasks" && !validID(in.ID) {
		return fail(400, "invalid_task", "清单项无效。")
	}
	return pgx.BeginFunc(ctx, a.db, func(tx pgx.Tx) error {
		if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock(hashtext('guitar-tasks:' || $1::text))`, user); err != nil {
			return err
		}
		const current = `user_id=$1 AND day=` + guitarToday
		switch in.Action {
		case "add-task":
			title, err := guitarText(in.Title, 1, 40)
			if err != nil {
				return err
			}
			if in.Minutes < 0 || in.Minutes > 600 {
				return fail(400, "invalid_duration", "请输入有效的练习时长。")
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
 FROM guitar_practice_tasks WHERE user_id=$1 AND day=`+guitarToday+`-1
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_tasks WHERE `+current+`)`, user)
			return err
		case "toggle-task":
			_, err := tx.Exec(ctx, `UPDATE guitar_practice_tasks SET done=$3 WHERE `+current+` AND id=$2`, user, in.ID, in.Done)
			return err
		case "remove-task":
			_, err := tx.Exec(ctx, `DELETE FROM guitar_practice_tasks WHERE `+current+` AND id=$2`, user, in.ID)
			return err
		case "move-task":
			if in.Direction != -1 && in.Direction != 1 {
				return fail(400, "invalid_direction", "移动方向无效。")
			}
			_, err := tx.Exec(ctx, `WITH current AS (SELECT id,position FROM guitar_practice_tasks WHERE `+current+` AND id=$2),
 neighbor AS (SELECT t.id,t.position FROM guitar_practice_tasks t,current c
 WHERE t.user_id=$1 AND t.day=`+guitarToday+` AND (t.position-c.position)*$3>0 ORDER BY abs(t.position-c.position) LIMIT 1)
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
	tag, err := a.db.Exec(r.Context(), `INSERT INTO guitar_reading_progress(user_id,score_id,page,page_count,read_at,learned)
 SELECT $1,id,GREATEST(1,$3),GREATEST(1,$4),CASE WHEN $3>0 THEN now() END,COALESCE($5,false)
 FROM guitar_scores WHERE app_id='guitar' AND id=$2 AND status='published'
 ON CONFLICT(app_id,user_id,score_id) DO UPDATE SET
 page=CASE WHEN $3>0 THEN EXCLUDED.page ELSE guitar_reading_progress.page END,
 page_count=CASE WHEN $3>0 THEN EXCLUDED.page_count ELSE guitar_reading_progress.page_count END,
 read_at=COALESCE(EXCLUDED.read_at,guitar_reading_progress.read_at),
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
