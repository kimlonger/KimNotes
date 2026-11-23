ALTER TABLE guitar_practice_sessions ADD COLUMN task_id uuid;
CREATE INDEX guitar_practice_score_task ON guitar_practice_sessions(user_id,task_id)
 WHERE task_id IS NOT NULL AND ended_at IS NOT NULL AND seconds>0;

-- Keep the event's day and title after a task is removed from the checklist.
CREATE TABLE guitar_practice_task_sessions (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 user_id uuid NOT NULL,
 id uuid NOT NULL,
 task_id uuid NOT NULL,
 day date NOT NULL,
 title text NOT NULL CHECK(char_length(title) BETWEEN 1 AND 40),
 seconds integer NOT NULL CHECK(seconds BETWEEN 1 AND 36000),
 recorded_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,user_id,id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE INDEX guitar_practice_task_session_owner_task ON guitar_practice_task_sessions(user_id,task_id);
