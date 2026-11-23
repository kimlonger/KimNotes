CREATE TABLE note_todos (
  app_id text NOT NULL CHECK (app_id = 'kimnotes'),
  user_id uuid NOT NULL,
  id uuid NOT NULL,
  revision bigint NOT NULL CHECK (revision > 0),
  body text NOT NULL CHECK (octet_length(body) <= 262144),
  created_at timestamptz,
  due_at timestamptz,
  done boolean NOT NULL,
  done_at timestamptz,
  repeat_rule smallint NOT NULL CHECK (repeat_rule BETWEEN 0 AND 3),
  deleted_at timestamptz,
  PRIMARY KEY (app_id, user_id, id),
  FOREIGN KEY (app_id, user_id) REFERENCES user_apps(app_id, user_id) ON DELETE CASCADE,
  CHECK (deleted_at IS NOT NULL OR (body <> '' AND created_at IS NOT NULL))
);

CREATE INDEX note_todos_visible_order ON note_todos (app_id, user_id, created_at DESC, id DESC)
  WHERE deleted_at IS NULL;

COMMENT ON TABLE note_todos IS '便签待办。由原设备同步，软删除后仍保留记录。';
