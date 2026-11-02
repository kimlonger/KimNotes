SET search_path TO kimcloud;

-- Mail links belong to the registered application, never a client parameter.
-- NULL retains the existing service-wide origin for the notes application.
ALTER TABLE applications ADD COLUMN IF NOT EXISTS public_origin text;

-- Identities stay in the existing shared users table. Main-site data is
-- application data, never a second identity store or a synthetic note.
INSERT INTO applications(id,name,public_origin) VALUES ('main','Kim 主站','https://kimlulu.com')
ON CONFLICT(id) DO UPDATE SET public_origin=COALESCE(applications.public_origin,EXCLUDED.public_origin);

CREATE TABLE IF NOT EXISTS main_account_follows (
  app_id text NOT NULL DEFAULT 'main' CHECK (app_id='main'),
  user_id uuid NOT NULL,
  project_ids text[] NOT NULL DEFAULT '{}' CHECK (cardinality(project_ids)<=64),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(app_id,user_id),
  FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);

-- Lifetime entry counts are independent of the bounded recent-event window.
CREATE TABLE IF NOT EXISTS main_account_totals (
  app_id text NOT NULL DEFAULT 'main' CHECK (app_id='main'),
  user_id uuid NOT NULL,
  project_id text NOT NULL CHECK (project_id ~ '^[a-z][a-z0-9-]{1,47}$'),
  entry_count bigint NOT NULL DEFAULT 0 CHECK (entry_count>=0),
  PRIMARY KEY(app_id,user_id,project_id),
  FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS main_account_entries (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  app_id text NOT NULL DEFAULT 'main' CHECK (app_id='main'),
  user_id uuid NOT NULL,
  project_id text NOT NULL CHECK (project_id ~ '^[a-z][a-z0-9-]{1,47}$'),
  action text NOT NULL CHECK (action IN ('open','download','source','details')),
  created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS main_account_entries_recent ON main_account_entries(app_id,user_id,id DESC);
