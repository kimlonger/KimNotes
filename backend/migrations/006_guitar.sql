-- Shared guitar resources deliberately do not depend on personal upload records.
INSERT INTO applications(id,name) VALUES('guitar','吉他学习') ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS guitar_courses (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id text NOT NULL,
 title text NOT NULL, summary text NOT NULL, level text NOT NULL,
 status text NOT NULL CHECK(status IN ('draft','published','planned','archived')),
 sort_order integer NOT NULL DEFAULT 0, updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,id), FOREIGN KEY(app_id) REFERENCES applications(id)
);
CREATE TABLE IF NOT EXISTS guitar_chapters (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id text NOT NULL,
 course_id text NOT NULL, title text NOT NULL, sort_order integer NOT NULL,
 PRIMARY KEY(app_id,id), FOREIGN KEY(app_id,course_id) REFERENCES guitar_courses(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_lessons (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id text NOT NULL,
 chapter_id text NOT NULL, title text NOT NULL, sort_order integer NOT NULL,
 content jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,id), FOREIGN KEY(app_id,chapter_id) REFERENCES guitar_chapters(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_scores (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id text NOT NULL,
 title text NOT NULL, description text NOT NULL DEFAULT '', category text NOT NULL,
 difficulty text NOT NULL, notation jsonb, mime text, bytes bytea, credit text NOT NULL,
 status text NOT NULL DEFAULT 'published' CHECK(status IN ('published','archived')),
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(app_id,id),
 CHECK((notation IS NOT NULL AND bytes IS NULL) OR (notation IS NULL AND bytes IS NOT NULL))
);
CREATE TABLE IF NOT EXISTS guitar_uploads (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id uuid NOT NULL,
 user_id uuid NOT NULL, title text NOT NULL, description text NOT NULL DEFAULT '',
 category text NOT NULL, difficulty text NOT NULL, filename text NOT NULL,
 mime text NOT NULL CHECK(mime IN ('image/png','image/jpeg','image/webp','application/pdf')),
 bytes bytea NOT NULL, status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','approved','rejected')),
 review_note text NOT NULL DEFAULT '', published_score_id text, created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,published_score_id) REFERENCES guitar_scores(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_favorites (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), user_id uuid NOT NULL,
 kind text NOT NULL CHECK(kind IN ('course','lesson','score')), target_id text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(app_id,user_id,kind,target_id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS guitar_progress (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), user_id uuid NOT NULL,
 lesson_id text NOT NULL, completed boolean NOT NULL DEFAULT false,
 checks jsonb NOT NULL DEFAULT '[]', updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,user_id,lesson_id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,lesson_id) REFERENCES guitar_lessons(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_visits (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), user_id uuid NOT NULL,
 visit_date date NOT NULL, PRIMARY KEY(app_id,user_id,visit_date),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS guitar_messages (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id uuid NOT NULL,
 nickname text NOT NULL, body text NOT NULL, reply text NOT NULL DEFAULT '',
 status text NOT NULL DEFAULT 'open' CHECK(status IN ('open','accepted','done','hidden')),
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_questions (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id uuid NOT NULL,
 user_id uuid NOT NULL, course_id text NOT NULL, lesson_id text, body text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(app_id,id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,course_id) REFERENCES guitar_courses(app_id,id),
 FOREIGN KEY(app_id,lesson_id) REFERENCES guitar_lessons(app_id,id)
);
CREATE TABLE IF NOT EXISTS guitar_replies (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id uuid NOT NULL,
 user_id uuid NOT NULL, question_id uuid NOT NULL, body text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(app_id,id),
 FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,question_id) REFERENCES guitar_questions(app_id,id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS guitar_uploads_owner ON guitar_uploads(app_id,user_id,created_at DESC);
CREATE INDEX IF NOT EXISTS guitar_questions_course ON guitar_questions(app_id,course_id,created_at DESC);
CREATE INDEX IF NOT EXISTS guitar_messages_recent ON guitar_messages(app_id,created_at DESC);
