CREATE TABLE guitar_practice_preferences (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 user_id uuid NOT NULL,
 weekly_minutes integer NOT NULL DEFAULT 150 CHECK(weekly_minutes BETWEEN 100 AND 600),
 weekly_days integer NOT NULL DEFAULT 5 CHECK(weekly_days BETWEEN 1 AND 7),
 first_tuned boolean NOT NULL DEFAULT false,
 PRIMARY KEY(app_id,user_id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);

CREATE TABLE guitar_practice_tasks (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 id uuid NOT NULL,
 user_id uuid NOT NULL,
 day date NOT NULL,
 title text NOT NULL CHECK(char_length(title) BETWEEN 1 AND 40),
 minutes integer NOT NULL DEFAULT 0 CHECK(minutes BETWEEN 0 AND 600),
 position integer NOT NULL,
 done boolean NOT NULL DEFAULT false,
 PRIMARY KEY(app_id,id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE INDEX guitar_practice_tasks_owner_day ON guitar_practice_tasks(app_id,user_id,day DESC,position);

CREATE TABLE guitar_practice_logs (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 id uuid NOT NULL,
 user_id uuid NOT NULL,
 day date NOT NULL,
 title text NOT NULL CHECK(char_length(title) BETWEEN 1 AND 40),
 minutes integer NOT NULL CHECK(minutes BETWEEN 1 AND 600),
 note text NOT NULL DEFAULT '' CHECK(char_length(note)<=60),
 PRIMARY KEY(app_id,id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE INDEX guitar_practice_logs_owner_day ON guitar_practice_logs(app_id,user_id,day DESC);

CREATE TABLE guitar_reading_progress (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 user_id uuid NOT NULL,
 score_id text NOT NULL,
 page integer NOT NULL DEFAULT 1 CHECK(page BETWEEN 1 AND 10000),
 page_count integer NOT NULL DEFAULT 1 CHECK(page_count BETWEEN page AND 10000),
 read_at timestamptz,
 learned boolean NOT NULL DEFAULT false,
 PRIMARY KEY(app_id,user_id,score_id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,score_id) REFERENCES guitar_scores(app_id,id) ON DELETE CASCADE
);
CREATE INDEX guitar_reading_progress_owner_recent ON guitar_reading_progress(app_id,user_id,read_at DESC);

ALTER TABLE guitar_practice_sessions ADD COLUMN automatic boolean NOT NULL DEFAULT false;
ALTER TABLE guitar_messages ADD COLUMN requested_title text;
ALTER TABLE guitar_messages ADD COLUMN requested_artist text;
