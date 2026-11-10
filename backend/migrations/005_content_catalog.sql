SET search_path TO kimcloud;

-- Public editorial content is separate from authentication applications and
-- per-user application data. No API exposes writes to these tables.
CREATE TABLE IF NOT EXISTS content_works (
  id text PRIMARY KEY CHECK (id ~ '^[a-z][a-z0-9-]{1,47}$'),
  kind text NOT NULL CHECK (kind IN ('app','plugin','tool')),
  status text NOT NULL DEFAULT 'published' CHECK (status IN ('draft','published','archived')),
  payload jsonb NOT NULL CHECK (jsonb_typeof(payload)='object' AND payload ? 'id' AND jsonb_typeof(payload->'id')='string' AND payload->>'id'=id AND octet_length(payload::text)<=131072),
  sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order>=0),
  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE IF NOT EXISTS content_articles (
  id text PRIMARY KEY CHECK (id ~ '^[a-z][a-z0-9-]{1,47}$'),
  project_id text REFERENCES content_works(id),
  status text NOT NULL DEFAULT 'published' CHECK (status IN ('draft','published','archived')),
  payload jsonb NOT NULL CHECK (jsonb_typeof(payload)='object' AND payload ? 'id' AND jsonb_typeof(payload->'id')='string' AND payload->>'id'=id AND octet_length(payload::text)<=131072),
  sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order>=0),
  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE IF NOT EXISTS content_updates (
  id text PRIMARY KEY CHECK (id ~ '^[a-z][a-z0-9-]{1,47}$'),
  project_id text NOT NULL REFERENCES content_works(id),
  article_id text REFERENCES content_articles(id),
  status text NOT NULL DEFAULT 'published' CHECK (status IN ('draft','published','archived')),
  payload jsonb NOT NULL CHECK (jsonb_typeof(payload)='object' AND payload ? 'id' AND jsonb_typeof(payload->'id')='string' AND payload->>'id'=id AND octet_length(payload::text)<=131072),
  sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order>=0),
  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS content_works_published ON content_works(status,sort_order,id);
CREATE INDEX IF NOT EXISTS content_articles_published ON content_articles(status,sort_order,id);
CREATE INDEX IF NOT EXISTS content_updates_published ON content_updates(status,sort_order,id);
