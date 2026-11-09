-- Keep legacy labels for older clients and imports; new ratings use half-star precision.
ALTER TABLE guitar_scores ADD COLUMN difficulty_stars numeric
 CHECK(difficulty_stars BETWEEN 0.5 AND 5 AND difficulty_stars*2=trunc(difficulty_stars*2));
ALTER TABLE guitar_uploads ADD COLUMN difficulty_stars numeric
 CHECK(difficulty_stars BETWEEN 0.5 AND 5 AND difficulty_stars*2=trunc(difficulty_stars*2));
UPDATE guitar_scores SET difficulty_stars=CASE difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END;
UPDATE guitar_uploads SET difficulty_stars=CASE difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END;
CREATE INDEX guitar_scores_star_filters ON guitar_scores(app_id,category,difficulty_stars,created_at DESC,id) WHERE status='published';
CREATE INDEX guitar_messages_priority ON guitar_messages(app_id,(status='open') DESC,created_at DESC,id DESC) WHERE status<>'hidden';
COMMENT ON COLUMN guitar_scores.difficulty_stars IS '曲谱难度，0.5到5星，步长0.5；空值兼容旧导入';
COMMENT ON COLUMN guitar_uploads.difficulty_stars IS '上传时选择的难度，审核通过后复制到公共曲谱';
