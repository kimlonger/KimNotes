CREATE TABLE guitar_members (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), user_id uuid NOT NULL,
 nickname text NOT NULL DEFAULT '琴友' CHECK(char_length(nickname) BETWEEN 1 AND 20),
 avatar text NOT NULL DEFAULT 'guitar' CHECK(avatar IN ('guitar','vinyl','cat','leaf','wave','sun')),
 avatar_mime text, avatar_bytes bytea, updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,user_id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 CHECK((avatar_bytes IS NULL AND avatar_mime IS NULL) OR (octet_length(avatar_bytes)<=524288 AND avatar_mime IN ('image/png','image/jpeg','image/webp')))
);
ALTER TABLE auth_challenges ADD COLUMN guitar_nickname text NOT NULL DEFAULT '';
ALTER TABLE auth_challenges ADD COLUMN guitar_avatar text NOT NULL DEFAULT '';
ALTER TABLE guitar_messages ADD COLUMN author_id uuid REFERENCES users(id) ON DELETE SET NULL;
CREATE TABLE guitar_practice_sessions (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'), id uuid NOT NULL,
 user_id uuid NOT NULL, score_id text NOT NULL, started_at timestamptz NOT NULL DEFAULT now(),
 ended_at timestamptz, seconds integer NOT NULL DEFAULT 0 CHECK(seconds BETWEEN 0 AND 21600),
 PRIMARY KEY(app_id,id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,score_id) REFERENCES guitar_scores(app_id,id)
);
CREATE INDEX guitar_practice_owner_recent ON guitar_practice_sessions(app_id,user_id,started_at DESC);
