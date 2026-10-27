CREATE SCHEMA IF NOT EXISTS kimcloud;
SET search_path TO kimcloud;

CREATE TABLE IF NOT EXISTS applications (
  id text PRIMARY KEY CHECK (id ~ '^[a-z][a-z0-9-]{1,47}$'),
  name text NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO applications(id,name) VALUES ('kimnotes','小羊便签') ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS users (
  id uuid PRIMARY KEY, email text NOT NULL UNIQUE, password_hash text NOT NULL,
  email_verified boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS user_apps (
  app_id text NOT NULL REFERENCES applications(id), user_id uuid NOT NULL REFERENCES users(id),
  cursor bigint NOT NULL DEFAULT 0, created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id)
);
CREATE TABLE IF NOT EXISTS sessions (
  token_hash text PRIMARY KEY, app_id text NOT NULL, user_id uuid NOT NULL,
  client_kind text NOT NULL CHECK(client_kind IN ('web','desktop')), device_name text NOT NULL,
  expires_at timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id)
);
CREATE INDEX IF NOT EXISTS sessions_user ON sessions(user_id);
CREATE TABLE IF NOT EXISTS auth_challenges (
  id uuid PRIMARY KEY, app_id text NOT NULL REFERENCES applications(id), email text NOT NULL,
  purpose text NOT NULL CHECK(purpose IN ('register','reset')), token_hash text NOT NULL,
  password_hash text NOT NULL DEFAULT '', attempts integer NOT NULL DEFAULT 0,
  expires_at timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS rate_limits (
  key text PRIMARY KEY, window_start timestamptz NOT NULL, count integer NOT NULL
);
CREATE TABLE IF NOT EXISTS notes (
  app_id text NOT NULL, user_id uuid NOT NULL, id uuid NOT NULL,
  revision bigint NOT NULL CHECK(revision > 0), document jsonb NOT NULL,
  conflict_of uuid, deleted_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id,id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id)
);
CREATE TABLE IF NOT EXISTS note_versions (
  app_id text NOT NULL, user_id uuid NOT NULL, note_id uuid NOT NULL, revision bigint NOT NULL,
  snapshot jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id,note_id,revision),
  FOREIGN KEY(app_id,user_id,note_id) REFERENCES notes(app_id,user_id,id)
);
CREATE TABLE IF NOT EXISTS note_changes (
  app_id text NOT NULL, user_id uuid NOT NULL, sequence bigint NOT NULL,
  note_id uuid NOT NULL, revision bigint NOT NULL,
  PRIMARY KEY(app_id,user_id,sequence),
  FOREIGN KEY(app_id,user_id,note_id,revision) REFERENCES note_versions(app_id,user_id,note_id,revision)
);
CREATE TABLE IF NOT EXISTS operations (
  app_id text NOT NULL, user_id uuid NOT NULL, id uuid NOT NULL,
  request_hash text NOT NULL, response jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id,id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id)
);
-- Private image bytes stay in PostgreSQL in the first release. No public bucket URLs.
-- A database backup includes both notes and their images; storage can later move
-- behind this same authenticated object API without changing note documents.
CREATE TABLE IF NOT EXISTS attachments (
  app_id text NOT NULL, user_id uuid NOT NULL, id uuid NOT NULL,
  mime text NOT NULL, sha256 text NOT NULL, bytes bytea NOT NULL,
  width integer NOT NULL, height integer NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id,id), FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id)
);
CREATE INDEX IF NOT EXISTS attachments_hash ON attachments(app_id,user_id,sha256);
