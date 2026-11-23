LOCK TABLE guitar_scores,guitar_uploads,guitar_score_pages IN ACCESS EXCLUSIVE MODE;

ALTER TABLE guitar_scores ADD COLUMN file_format text NOT NULL DEFAULT 'notation';
ALTER TABLE guitar_uploads
 ADD COLUMN score_id text,
 ADD COLUMN original_metadata jsonb CHECK(jsonb_typeof(original_metadata)='object');

UPDATE guitar_uploads
 SET score_id=COALESCE(published_score_id,'upload-'||id::text);

DO $$
BEGIN
 IF EXISTS (
  SELECT 1 FROM guitar_uploads u LEFT JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)
  WHERE s.id IS NULL OR (u.status='approved') IS DISTINCT FROM (u.published_score_id IS NOT NULL)
   OR (u.status IN ('pending','rejected') AND (s.status<>'private' OR s.bytes IS NOT NULL))
   OR (u.status='approved' AND s.status='private')
 ) OR EXISTS (
  SELECT 1 FROM guitar_scores s WHERE s.status='private' AND NOT EXISTS (
   SELECT 1 FROM guitar_uploads u WHERE (u.app_id,u.score_id)=(s.app_id,s.id) AND u.status IN ('pending','rejected')
  )
 ) THEN
  RAISE EXCEPTION 'guitar file migration requires a valid score for every submission';
 END IF;
END $$;

-- Approval used to copy files. Preserve a separate original only when the
-- old upload and its public revision really had different media.
CREATE TEMP TABLE guitar_distinct_originals ON COMMIT DROP AS
SELECT u.app_id,u.id
FROM guitar_uploads u JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)
WHERE u.status='approved' AND (
 (u.mime,u.bytes) IS DISTINCT FROM (s.mime,s.bytes)
 OR EXISTS (
  SELECT page,mime,bytes FROM guitar_score_pages p WHERE p.app_id=u.app_id AND p.upload_id=u.id
  EXCEPT
  SELECT page,mime,bytes FROM guitar_score_pages p WHERE p.app_id=s.app_id AND p.score_id=s.id
 )
 OR EXISTS (
  SELECT page,mime,bytes FROM guitar_score_pages p WHERE p.app_id=s.app_id AND p.score_id=s.id
  EXCEPT
  SELECT page,mime,bytes FROM guitar_score_pages p WHERE p.app_id=u.app_id AND p.upload_id=u.id
 )
);

-- Old admin edits can legitimately make an approved original differ from the
-- public metadata. Retain those differences for the personal record and export.
UPDATE guitar_uploads u SET original_metadata=jsonb_build_object(
 'title',u.title,'description',u.description,'category',u.category,'difficulty',u.difficulty,
 'artist',u.artist,'genre',u.genre,'difficulty_stars',u.difficulty_stars,'attributes',u.attributes)
FROM guitar_scores s
WHERE (s.app_id,s.id)=(u.app_id,u.score_id) AND u.status='approved'
 AND (u.title,u.description,u.category,u.difficulty,u.artist,u.genre,u.difficulty_stars,u.attributes)
     IS DISTINCT FROM (s.title,s.description,s.category,s.difficulty,s.artist,s.genre,s.difficulty_stars,s.attributes);

DROP TRIGGER guitar_uploads_archive_withdrawn_score ON guitar_uploads;
DROP TRIGGER guitar_uploads_normalize_metadata ON guitar_uploads;
DROP FUNCTION guitar_normalize_upload_metadata();

UPDATE guitar_scores SET file_format=CASE WHEN mime='application/pdf' THEN 'pdf' ELSE 'images' END
WHERE bytes IS NOT NULL;

-- The upload was the source of the private bytes and user-entered details.
-- The established score ID and any linked practice and reading data stay put.
ALTER TABLE guitar_scores DROP CONSTRAINT guitar_scores_check;
UPDATE guitar_scores s SET file_format=CASE WHEN u.mime='application/pdf' THEN 'pdf' ELSE 'images' END,
 notation=NULL,title=u.title,description=u.description,category=u.category,difficulty=u.difficulty,
 artist=u.artist,genre=u.genre,difficulty_stars=u.difficulty_stars,
 attributes=guitar_resolved_metadata(u.attributes||s.attributes,NULL,u.description)
FROM guitar_uploads u WHERE (s.app_id,s.id)=(u.app_id,u.score_id) AND s.status='private';

-- Rename in place so runtime grants and the existing page IDs survive.
ALTER TABLE guitar_score_pages RENAME TO guitar_score_files;
ALTER TABLE guitar_score_files
 DROP CONSTRAINT guitar_score_pages_owner,
 DROP CONSTRAINT guitar_score_pages_upload_ref,
 DROP CONSTRAINT guitar_score_pages_upload_unique,
 DROP CONSTRAINT guitar_score_pages_score_unique,
 DROP CONSTRAINT guitar_score_pages_page_check,
 DROP CONSTRAINT guitar_score_pages_mime_check;
ALTER TABLE guitar_score_files RENAME COLUMN page TO file_no;
ALTER TABLE guitar_score_files RENAME COLUMN upload_id TO original_upload_id;

DELETE FROM guitar_score_files p USING guitar_uploads u
WHERE (p.app_id,p.original_upload_id)=(u.app_id,u.id) AND u.status='approved'
 AND NOT EXISTS(SELECT 1 FROM guitar_distinct_originals o WHERE (o.app_id,o.id)=(u.app_id,u.id));

UPDATE guitar_score_files p SET score_id=u.score_id,
 original_upload_id=CASE WHEN u.status='approved' THEN u.id END
FROM guitar_uploads u WHERE (p.app_id,p.original_upload_id)=(u.app_id,u.id);

INSERT INTO guitar_score_files(app_id,score_id,file_no,mime,bytes)
SELECT app_id,id,1,mime,bytes FROM guitar_scores WHERE bytes IS NOT NULL;
INSERT INTO guitar_score_files(app_id,score_id,file_no,original_upload_id,mime,bytes)
SELECT u.app_id,u.score_id,1,o.id,u.mime,u.bytes
FROM guitar_uploads u LEFT JOIN guitar_distinct_originals o ON (o.app_id,o.id)=(u.app_id,u.id)
WHERE u.status<>'approved' OR o.id IS NOT NULL;

ALTER TABLE guitar_uploads
 ALTER COLUMN score_id SET NOT NULL,
 ADD CONSTRAINT guitar_uploads_score_ref FOREIGN KEY(app_id,score_id) REFERENCES guitar_scores(app_id,id),
 ADD CONSTRAINT guitar_uploads_original_key UNIQUE(app_id,id,score_id);
CREATE UNIQUE INDEX guitar_uploads_private_score ON guitar_uploads(app_id,score_id) WHERE status IN ('pending','rejected');

ALTER TABLE guitar_score_files
 ALTER COLUMN score_id SET NOT NULL,
 ADD CONSTRAINT guitar_score_files_original_ref FOREIGN KEY(app_id,original_upload_id,score_id)
  REFERENCES guitar_uploads(app_id,id,score_id) ON DELETE CASCADE,
 ADD CONSTRAINT guitar_score_files_valid CHECK(
  octet_length(bytes)>0 AND file_no BETWEEN 1 AND 12 AND
  (mime IN ('image/png','image/jpeg','image/webp') OR (mime='application/pdf' AND file_no=1))
 );
CREATE UNIQUE INDEX guitar_score_files_number ON guitar_score_files(app_id,score_id,file_no) WHERE original_upload_id IS NULL;
CREATE UNIQUE INDEX guitar_score_files_original_number ON guitar_score_files(app_id,original_upload_id,file_no) WHERE original_upload_id IS NOT NULL;

ALTER TABLE guitar_scores DROP COLUMN mime, DROP COLUMN bytes,
 ADD CONSTRAINT guitar_scores_format_check CHECK(
  (file_format='notation' AND notation IS NOT NULL AND status<>'private')
  OR (file_format IN ('images','pdf') AND notation IS NULL)
  OR (file_format='none' AND notation IS NULL AND status='archived')
 );
ALTER TABLE guitar_uploads
 DROP COLUMN title,DROP COLUMN description,DROP COLUMN category,DROP COLUMN difficulty,
 DROP COLUMN mime,DROP COLUMN bytes,DROP COLUMN published_score_id,DROP COLUMN artist,
 DROP COLUMN genre,DROP COLUMN difficulty_stars,DROP COLUMN attributes;

CREATE OR REPLACE FUNCTION archive_withdrawn_guitar_upload() RETURNS trigger
LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
BEGIN
 UPDATE guitar_scores
 SET status='archived',file_format='none',withdrawn_upload_owner_id=OLD.user_id
 WHERE (app_id,id)=(OLD.app_id,OLD.score_id) AND status='private';
 IF FOUND THEN
  DELETE FROM guitar_score_files WHERE (app_id,score_id)=(OLD.app_id,OLD.score_id) AND original_upload_id IS NULL;
 END IF;
 RETURN OLD;
END;
$$;
CREATE TRIGGER guitar_uploads_archive_withdrawn_score
BEFORE DELETE ON guitar_uploads FOR EACH ROW
WHEN (OLD.status IN ('pending','rejected'))
EXECUTE FUNCTION archive_withdrawn_guitar_upload();
