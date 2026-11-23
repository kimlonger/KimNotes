CREATE INDEX guitar_practice_history_order ON guitar_practice_sessions(
 app_id,user_id,(COALESCE(practice_day,(started_at AT TIME ZONE 'Asia/Shanghai')::date)) DESC,
 (CASE WHEN record_kind IN ('score','task') THEN started_at ELSE 'infinity'::timestamptz END) DESC,
 record_kind DESC,id DESC
) WHERE ended_at IS NOT NULL AND seconds>0;
