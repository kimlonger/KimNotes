-- Automatic artist resolution also covers existing import and upload-review paths.
CREATE TABLE guitar_artists (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 id uuid NOT NULL DEFAULT gen_random_uuid(),
 name text NOT NULL CHECK(char_length(name) BETWEEN 1 AND 120),
 name_key text NOT NULL CHECK(name_key<>''),
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,id), UNIQUE(app_id,name_key),
 FOREIGN KEY(app_id) REFERENCES applications(id)
);

ALTER TABLE guitar_scores ADD COLUMN artist_id uuid,
 ADD CONSTRAINT guitar_scores_artist_ref FOREIGN KEY(app_id,artist_id) REFERENCES guitar_artists(app_id,id);

CREATE FUNCTION guitar_resolve_artist() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
 display_name text;
 normalized_name text;
BEGIN
 display_name := btrim(regexp_replace(NEW.artist, '[[:space:]　]+', ' ', 'g'));
 normalized_name := lower(regexp_replace(display_name, '[[:space:]　]+', '', 'g'));
 IF normalized_name='' THEN
   NEW.artist_id := NULL;
 ELSE
   INSERT INTO guitar_artists(app_id,name,name_key)
   VALUES(NEW.app_id,display_name,normalized_name)
   ON CONFLICT(app_id,name_key) DO NOTHING;
   SELECT id INTO NEW.artist_id FROM guitar_artists
   WHERE app_id=NEW.app_id AND name_key=normalized_name;
 END IF;
 RETURN NEW;
END;
$$;

CREATE TRIGGER guitar_scores_resolve_artist
 BEFORE INSERT OR UPDATE OF artist,artist_id ON guitar_scores
 FOR EACH ROW EXECUTE FUNCTION guitar_resolve_artist();

UPDATE guitar_scores SET artist=artist WHERE artist<>'';

CREATE INDEX guitar_scores_artist_public ON guitar_scores(app_id,artist_id,created_at DESC,id)
 WHERE status='published';
CREATE INDEX guitar_favorites_score_activity ON guitar_favorites(app_id,target_id,created_at DESC)
 WHERE kind='score';
CREATE INDEX guitar_visits_period ON guitar_visits(app_id,visit_date DESC,user_id);

COMMENT ON TABLE guitar_artists IS '自动归并的歌手署名，一对多关联公开曲谱，无人工推荐权重';
