CREATE TABLE note_profiles (
  app_id text NOT NULL DEFAULT 'kimnotes' CHECK (app_id = 'kimnotes'),
  user_id uuid NOT NULL,
  nickname text NOT NULL CHECK (char_length(nickname) BETWEEN 1 AND 24),
  avatar_id text NOT NULL CHECK (avatar_id IN ('sheep', 'sky', 'mint', 'peach', 'lilac', 'gray')),
  PRIMARY KEY (app_id, user_id),
  FOREIGN KEY (app_id, user_id) REFERENCES user_apps(app_id, user_id) ON DELETE CASCADE
);

INSERT INTO note_profiles (app_id, user_id, nickname, avatar_id)
SELECT app_id, user_id, '小羊用户', 'sheep' FROM user_apps WHERE app_id = 'kimnotes';

ALTER TABLE auth_challenges
  ADD COLUMN note_nickname text NOT NULL DEFAULT '',
  ADD COLUMN note_avatar_id text NOT NULL DEFAULT '';

COMMENT ON TABLE note_profiles IS '小羊便签个人资料。按便签用户保存昵称与预设头像。';
