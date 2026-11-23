-- Preserve every source's public record ID, including IDs reused by another
-- account or another practice source. Existing score sessions keep their IDs.
ALTER TABLE guitar_practice_sessions
 ADD COLUMN record_kind text NOT NULL DEFAULT 'score',
 ADD COLUMN practice_day date,
 ADD COLUMN title text,
 ADD COLUMN note text NOT NULL DEFAULT '';
ALTER TABLE guitar_practice_sessions ALTER COLUMN score_id DROP NOT NULL;
ALTER TABLE guitar_practice_sessions DROP CONSTRAINT guitar_practice_sessions_pkey;
ALTER TABLE guitar_practice_sessions ADD PRIMARY KEY(app_id,user_id,record_kind,id);
ALTER TABLE guitar_practice_sessions DROP CONSTRAINT guitar_practice_sessions_seconds_check;
ALTER TABLE guitar_practice_sessions ADD CONSTRAINT guitar_practice_sessions_seconds_check
 CHECK(seconds BETWEEN 0 AND 36000);

INSERT INTO guitar_practice_sessions(app_id,user_id,record_kind,id,practice_day,title,note,
 started_at,ended_at,seconds)
SELECT app_id,user_id,'manual',id,day,title,note,
 day::timestamp AT TIME ZONE 'Asia/Shanghai',day::timestamp AT TIME ZONE 'Asia/Shanghai',minutes*60
FROM guitar_practice_logs;

INSERT INTO guitar_practice_sessions(app_id,user_id,record_kind,id,task_id,practice_day,title,
 started_at,ended_at,seconds)
SELECT app_id,user_id,'task',id,task_id,day,title,recorded_at,recorded_at,seconds
FROM guitar_practice_task_sessions;

-- Old completed checklist items contributed their planned time only while no
-- actual timer had completed. Materialize that contribution once at migration.
INSERT INTO guitar_practice_sessions(app_id,user_id,record_kind,id,task_id,practice_day,title,
 started_at,ended_at,seconds)
SELECT t.app_id,t.user_id,'estimate',t.id,t.id,t.day,t.title,
 t.day::timestamp AT TIME ZONE 'Asia/Shanghai',t.day::timestamp AT TIME ZONE 'Asia/Shanghai',t.minutes*60
FROM guitar_practice_tasks t
WHERE t.done AND t.minutes>0
 AND NOT EXISTS(SELECT 1 FROM guitar_practice_sessions p
  WHERE p.app_id=t.app_id AND p.user_id=t.user_id AND p.task_id=t.id
   AND p.record_kind IN ('score','task') AND p.ended_at IS NOT NULL AND p.seconds>0);

ALTER TABLE guitar_practice_sessions ADD CONSTRAINT guitar_practice_sessions_source_check CHECK(
 (record_kind='score' AND score_id IS NOT NULL AND practice_day IS NULL)
 OR (record_kind IN ('manual','task','estimate') AND score_id IS NULL
  AND practice_day IS NOT NULL AND title IS NOT NULL AND char_length(title) BETWEEN 1 AND 40
  AND ended_at IS NOT NULL AND seconds>0
  AND ((record_kind='manual' AND task_id IS NULL)
   OR (record_kind IN ('task','estimate') AND task_id IS NOT NULL)))
);
ALTER TABLE guitar_practice_sessions ADD CONSTRAINT guitar_practice_sessions_note_check
 CHECK(char_length(note)<=60);
CREATE INDEX guitar_practice_owner_day ON guitar_practice_sessions(app_id,user_id,practice_day DESC)
 WHERE record_kind<>'score';
CREATE UNIQUE INDEX guitar_practice_task_estimate ON guitar_practice_sessions(app_id,user_id,task_id)
 WHERE record_kind='estimate';

-- Keep task identity for delayed, account-scoped timer uploads from installed
-- clients. Checklist reads and edits filter deleted_at instead of losing it.
ALTER TABLE guitar_practice_tasks ADD COLUMN deleted_at timestamptz;

DROP TABLE guitar_practice_logs;
DROP TABLE guitar_practice_task_sessions;
