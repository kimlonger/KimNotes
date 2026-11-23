-- Stop the writers and take a fresh backup first. This reconstructs 034 from
-- the current rows, including uploads and practice saved after the cutover.
BEGIN;
SET LOCAL search_path TO kimcloud, public;
SET LOCAL lock_timeout TO '5s';
SELECT pg_advisory_xact_lock(725194138);
LOCK TABLE guitar_members,guitar_scores,guitar_uploads,guitar_score_files,guitar_practice_sessions IN ACCESS EXCLUSIVE MODE;

DROP INDEX guitar_practice_history_order;
DROP TRIGGER guitar_uploads_archive_withdrawn_score ON guitar_uploads;
ALTER TABLE guitar_scores DISABLE TRIGGER guitar_scores_normalize_metadata;
ALTER TABLE guitar_scores ADD COLUMN mime text,ADD COLUMN bytes bytea;
UPDATE guitar_scores s SET mime=f.mime,bytes=f.bytes FROM guitar_score_files f
 WHERE (s.app_id,s.id)=(f.app_id,f.score_id) AND f.file_no=1 AND f.original_upload_id IS NULL AND s.status<>'private';
ALTER TABLE guitar_scores DROP CONSTRAINT guitar_scores_format_check;
UPDATE guitar_scores SET notation='{}'::jsonb
 WHERE status='private' OR file_format='none';
ALTER TABLE guitar_scores DROP COLUMN file_format,
 ADD CONSTRAINT guitar_scores_check CHECK((notation IS NOT NULL AND bytes IS NULL) OR (notation IS NULL AND bytes IS NOT NULL)),
 ADD CONSTRAINT guitar_scores_file_media_valid CHECK(
  (bytes IS NULL AND mime IS NULL) OR (bytes IS NOT NULL AND mime IS NOT NULL AND octet_length(bytes)>0
   AND mime IN ('image/png','image/jpeg','image/webp','application/pdf')));

ALTER TABLE guitar_uploads ADD COLUMN title text,ADD COLUMN description text NOT NULL DEFAULT '',
 ADD COLUMN category text,ADD COLUMN difficulty text,
 ADD COLUMN mime text,ADD COLUMN bytes bytea,ADD COLUMN published_score_id text,
 ADD COLUMN artist text NOT NULL DEFAULT '',ADD COLUMN genre text NOT NULL DEFAULT '',
 ADD COLUMN difficulty_stars numeric,ADD COLUMN attributes jsonb NOT NULL DEFAULT '{}';

WITH originals AS (
 SELECT u.app_id,u.id,f.mime,f.bytes
 FROM guitar_uploads u JOIN guitar_score_files f ON (f.app_id,f.score_id)=(u.app_id,u.score_id)
 WHERE f.file_no=1 AND f.original_upload_id IS NOT DISTINCT FROM (
  SELECT original_upload_id FROM guitar_score_files WHERE app_id=u.app_id AND original_upload_id=u.id LIMIT 1
 )
)
UPDATE guitar_uploads u SET title=COALESCE(u.original_metadata->>'title',s.title),
 description=COALESCE(u.original_metadata->>'description',s.description),
 category=COALESCE(u.original_metadata->>'category',s.category),
 difficulty=COALESCE(u.original_metadata->>'difficulty',s.difficulty),
 artist=COALESCE(u.original_metadata->>'artist',s.artist),genre=COALESCE(u.original_metadata->>'genre',s.genre),
 difficulty_stars=CASE WHEN u.original_metadata IS NOT NULL THEN (u.original_metadata->>'difficulty_stars')::numeric ELSE s.difficulty_stars END,
 attributes=COALESCE(u.original_metadata->'attributes',s.attributes),
 published_score_id=CASE WHEN u.status='approved' THEN u.score_id END,
 mime=o.mime,bytes=o.bytes
FROM guitar_scores s,originals o
WHERE (s.app_id,s.id)=(u.app_id,u.score_id) AND (o.app_id,o.id)=(u.app_id,u.id);

ALTER TABLE guitar_uploads ALTER COLUMN title SET NOT NULL,ALTER COLUMN category SET NOT NULL,
 ALTER COLUMN difficulty SET NOT NULL,ALTER COLUMN mime SET NOT NULL,ALTER COLUMN bytes SET NOT NULL,
 ADD CONSTRAINT guitar_uploads_app_id_published_score_id_fkey FOREIGN KEY(app_id,published_score_id) REFERENCES guitar_scores(app_id,id),
 ADD CONSTRAINT guitar_uploads_attributes_check CHECK(jsonb_typeof(attributes)='object'),
 ADD CONSTRAINT guitar_uploads_artist_length CHECK(char_length(artist)<=120),
 ADD CONSTRAINT guitar_uploads_category CHECK(category IN ('弹唱','指弹','练习')),
 ADD CONSTRAINT guitar_uploads_difficulty CHECK(difficulty IN ('入门','初级','中级')),
 ADD CONSTRAINT guitar_uploads_genre CHECK(genre IN ('','民谣','摇滚','其他')),
 ADD CONSTRAINT guitar_uploads_difficulty_stars_check CHECK(difficulty_stars BETWEEN 0.5 AND 5 AND difficulty_stars*2=trunc(difficulty_stars*2)),
 ADD CONSTRAINT guitar_uploads_mime_check CHECK(mime IN ('image/png','image/jpeg','image/webp','application/pdf'));

ALTER TABLE guitar_score_files DROP CONSTRAINT guitar_score_files_original_ref,
 DROP CONSTRAINT guitar_score_files_valid, ALTER COLUMN score_id DROP NOT NULL;
DROP INDEX guitar_score_files_number,guitar_score_files_original_number;
-- Recreate the duplicate upload side that the old API expects, only for
-- approved uploads that are using the canonical file set.
INSERT INTO guitar_score_files(app_id,score_id,original_upload_id,file_no,mime,bytes)
SELECT f.app_id,f.score_id,u.id,f.file_no,f.mime,f.bytes
FROM guitar_uploads u JOIN guitar_score_files f ON (f.app_id,f.score_id)=(u.app_id,u.score_id)
WHERE u.status='approved' AND f.original_upload_id IS NULL AND f.file_no>1 AND NOT EXISTS (
 SELECT 1 FROM guitar_score_files o WHERE (o.app_id,o.original_upload_id)=(u.app_id,u.id)
);
UPDATE guitar_score_files f SET original_upload_id=u.id
FROM guitar_uploads u WHERE (f.app_id,f.score_id)=(u.app_id,u.score_id)
 AND u.status IN ('pending','rejected') AND f.original_upload_id IS NULL;
DELETE FROM guitar_score_files WHERE file_no=1;
UPDATE guitar_score_files SET score_id=NULL WHERE original_upload_id IS NOT NULL;
ALTER TABLE guitar_score_files RENAME COLUMN original_upload_id TO upload_id;
ALTER TABLE guitar_score_files RENAME COLUMN file_no TO page;
ALTER TABLE guitar_score_files RENAME TO guitar_score_pages;
ALTER TABLE guitar_score_pages
 ADD CONSTRAINT guitar_score_pages_owner CHECK ((score_id IS NULL) <> (upload_id IS NULL)),
 ADD CONSTRAINT guitar_score_pages_score_unique UNIQUE(app_id,score_id,page),
 ADD CONSTRAINT guitar_score_pages_upload_unique UNIQUE(app_id,upload_id,page),
 ADD CONSTRAINT guitar_score_pages_page_check CHECK(page BETWEEN 2 AND 12),
 ADD CONSTRAINT guitar_score_pages_mime_check CHECK(mime IN ('image/png','image/jpeg','image/webp')),
 ADD CONSTRAINT guitar_score_pages_upload_ref FOREIGN KEY(app_id,upload_id) REFERENCES guitar_uploads(app_id,id) ON DELETE CASCADE;
ALTER TABLE guitar_uploads DROP COLUMN score_id,DROP COLUMN original_metadata;

UPDATE guitar_scores SET description='' WHERE status='private';
ALTER TABLE guitar_scores ENABLE TRIGGER guitar_scores_normalize_metadata;

CREATE FUNCTION guitar_normalize_upload_metadata() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
DECLARE
 inherited jsonb;
 field text;
BEGIN
 IF TG_OP='UPDATE' AND NEW.attributes IS NOT DISTINCT FROM OLD.attributes
  AND NEW.description IS DISTINCT FROM OLD.description THEN
  inherited := guitar_resolved_metadata('{}',NULL,OLD.description);
  FOREACH field IN ARRAY ARRAY['key','capo','meter','bpm','chords'] LOOP
   IF OLD.attributes->field=inherited->field THEN
    NEW.attributes := NEW.attributes-field;
   END IF;
  END LOOP;
 END IF;
 NEW.attributes := guitar_resolved_metadata(NEW.attributes,NULL,NEW.description);
 NEW.difficulty_stars := COALESCE(NEW.difficulty_stars,
  CASE NEW.difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END);
 RETURN NEW;
END;
$$;
CREATE TRIGGER guitar_uploads_normalize_metadata
BEFORE INSERT OR UPDATE OF attributes,description,difficulty,difficulty_stars ON guitar_uploads
FOR EACH ROW EXECUTE FUNCTION guitar_normalize_upload_metadata();

CREATE OR REPLACE FUNCTION archive_withdrawn_guitar_upload() RETURNS trigger
LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
BEGIN
 UPDATE guitar_scores SET status='archived',withdrawn_upload_owner_id=OLD.user_id,description=OLD.description
 WHERE app_id=OLD.app_id AND id='upload-'||OLD.id::text AND status='private';
 RETURN OLD;
END;
$$;
CREATE TRIGGER guitar_uploads_archive_withdrawn_score
BEFORE DELETE ON guitar_uploads FOR EACH ROW
WHEN (OLD.status IN ('pending','rejected'))
EXECUTE FUNCTION archive_withdrawn_guitar_upload();

CREATE TABLE guitar_practice_preferences(
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 user_id uuid NOT NULL,
 weekly_minutes integer NOT NULL DEFAULT 150 CHECK(weekly_minutes BETWEEN 100 AND 600),
 weekly_days integer NOT NULL DEFAULT 5 CHECK(weekly_days BETWEEN 1 AND 7),
 first_tuned boolean NOT NULL DEFAULT false,
 equipped_title text NOT NULL DEFAULT '' CHECK(equipped_title='' OR equipped_title ~ '^(songs|time)-[1-7]$'),
 PRIMARY KEY(app_id,user_id),FOREIGN KEY(app_id,user_id) REFERENCES user_apps(app_id,user_id) ON DELETE CASCADE
);
INSERT INTO guitar_practice_preferences
SELECT app_id,user_id,weekly_minutes,weekly_days,first_tuned,equipped_title FROM guitar_members
WHERE (weekly_minutes,weekly_days,first_tuned,equipped_title) IS DISTINCT FROM (150,5,false,''::text);

DO $$
DECLARE permission record;
BEGIN
 FOR permission IN
  SELECT grantee,privilege_type FROM information_schema.table_privileges
  WHERE table_schema=current_schema() AND table_name='guitar_members'
   AND grantee NOT IN ('PUBLIC',current_user) AND privilege_type IN ('SELECT','INSERT','UPDATE','DELETE')
 LOOP
  EXECUTE format('GRANT %s ON guitar_practice_preferences TO %I',permission.privilege_type,permission.grantee);
 END LOOP;
END $$;

ALTER TABLE guitar_members DROP COLUMN weekly_minutes,DROP COLUMN weekly_days,DROP COLUMN first_tuned,DROP COLUMN equipped_title;
-- Leave the new source data intact. 034 ignores it, and 035 can reuse it.
DELETE FROM schema_migrations WHERE version IN ('035_guitar_member_settings','036_guitar_score_files','037_guitar_practice_history');
COMMIT;
