SET search_path TO kimcloud;
ALTER TABLE users ADD COLUMN IF NOT EXISTS auth_version bigint NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN IF NOT EXISTS password_changed_at timestamptz;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS id uuid NOT NULL DEFAULT gen_random_uuid();
CREATE UNIQUE INDEX IF NOT EXISTS sessions_identity ON sessions(id);
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS protocol_version integer NOT NULL DEFAULT 1;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS device_id text NOT NULL DEFAULT '';
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS last_activity_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS last_rotation_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS idle_expires_at timestamptz;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS absolute_expires_at timestamptz;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS revoked_at timestamptz;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS auth_version bigint NOT NULL DEFAULT 0;
UPDATE sessions SET absolute_expires_at=LEAST(expires_at,created_at+interval '8 hours'),
 idle_expires_at=LEAST(expires_at,now()+interval '30 minutes'),
 expires_at=LEAST(expires_at,created_at+interval '8 hours') WHERE client_kind='web' AND absolute_expires_at IS NULL;
CREATE TABLE IF NOT EXISTS auth_policy (
 id integer PRIMARY KEY CHECK(id=1), legacy_upgrade_until timestamptz NOT NULL
);
INSERT INTO auth_policy(id,legacy_upgrade_until) VALUES(1,now()+interval '14 days') ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS access_tokens (
 token_hash text PRIMARY KEY,session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
 expires_at timestamptz NOT NULL,created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS access_tokens_session ON access_tokens(session_id);
CREATE TABLE IF NOT EXISTS refresh_tokens (
 token_hash text PRIMARY KEY,session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
 generation bigint NOT NULL,expires_at timestamptz NOT NULL,consumed_at timestamptz,
 request_id uuid,result bytea,result_expires_at timestamptz,replaced_by text,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS refresh_tokens_session ON refresh_tokens(session_id);
CREATE INDEX IF NOT EXISTS refresh_results_expiry ON refresh_tokens(result_expires_at) WHERE result IS NOT NULL;
CREATE INDEX IF NOT EXISTS access_tokens_expiry ON access_tokens(expires_at);
CREATE TABLE IF NOT EXISTS web_cookie_aliases (
 token_hash text PRIMARY KEY,session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
 expires_at timestamptz NOT NULL,result bytea NOT NULL
);
CREATE TABLE IF NOT EXISTS desktop_upgrades (
 legacy_hash text PRIMARY KEY,session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
 request_id uuid NOT NULL,result bytea NOT NULL,expires_at timestamptz NOT NULL
);
