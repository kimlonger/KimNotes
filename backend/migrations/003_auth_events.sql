SET search_path TO kimcloud;
CREATE TABLE IF NOT EXISTS auth_events (
 id bigserial PRIMARY KEY,user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 app_id text NOT NULL REFERENCES applications(id),session_id uuid,
 event text NOT NULL,created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS auth_events_user_time ON auth_events(user_id,created_at DESC);
