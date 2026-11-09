-- Reuse shared identities; public scores stay independent of personal uploads.
ALTER TABLE guitar_scores
 ADD COLUMN IF NOT EXISTS artist text NOT NULL DEFAULT '',
 ADD COLUMN IF NOT EXISTS genre text NOT NULL DEFAULT '',
 ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE guitar_uploads
 ADD COLUMN IF NOT EXISTS artist text NOT NULL DEFAULT '',
 ADD COLUMN IF NOT EXISTS genre text NOT NULL DEFAULT '',
 ADD COLUMN IF NOT EXISTS permission_confirmed_at timestamptz,
 ADD COLUMN IF NOT EXISTS reviewed_at timestamptz;
ALTER TABLE guitar_messages ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE guitar_scores
 ADD CONSTRAINT guitar_scores_artist_length CHECK(char_length(artist)<=120),
 ADD CONSTRAINT guitar_scores_genre CHECK(genre IN ('','民谣','摇滚','其他')),
 ADD CONSTRAINT guitar_scores_category CHECK(category IN ('弹唱','指弹','练习')),
 ADD CONSTRAINT guitar_scores_difficulty CHECK(difficulty IN ('入门','初级','中级'));
ALTER TABLE guitar_uploads
 ADD CONSTRAINT guitar_uploads_artist_length CHECK(char_length(artist)<=120),
 ADD CONSTRAINT guitar_uploads_genre CHECK(genre IN ('','民谣','摇滚','其他')),
 ADD CONSTRAINT guitar_uploads_category CHECK(category IN ('弹唱','指弹','练习')),
 ADD CONSTRAINT guitar_uploads_difficulty CHECK(difficulty IN ('入门','初级','中级'));

CREATE INDEX IF NOT EXISTS guitar_scores_published_recent
 ON guitar_scores(app_id,created_at DESC,id) WHERE status='published';
CREATE INDEX IF NOT EXISTS guitar_scores_published_filters
 ON guitar_scores(app_id,category,difficulty,created_at DESC,id) WHERE status='published';
CREATE INDEX IF NOT EXISTS guitar_uploads_pending
 ON guitar_uploads(app_id,created_at,id) WHERE status='pending';
CREATE INDEX IF NOT EXISTS guitar_favorites_owner_recent
 ON guitar_favorites(app_id,user_id,created_at DESC);

COMMENT ON TABLE guitar_scores IS '独立公共曲谱，不依赖个人上传记录';
COMMENT ON COLUMN guitar_scores.artist IS '歌手或作者，未知时留空';
COMMENT ON COLUMN guitar_uploads.permission_confirmed_at IS '本次上传确认分享权限的时间，旧记录允许为空';
COMMENT ON COLUMN guitar_uploads.reviewed_at IS '实际审核时间，不为历史记录虚构时间';
COMMENT ON TABLE guitar_visits IS '按上海日期每日打开去重，头衔由累计天数计算';
COMMENT ON TABLE guitar_messages IS '建议模块，可匿名提交，hidden 不公开展示';
