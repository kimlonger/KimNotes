ALTER TABLE guitar_scores ADD COLUMN withdrawn_upload_owner_id uuid REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE guitar_scores ADD CONSTRAINT guitar_scores_withdrawn_owner_check
 CHECK(withdrawn_upload_owner_id IS NULL OR (status='archived' AND id LIKE 'upload-%'));

-- Reconcile any upload committed by an older process after migration 025.
INSERT INTO guitar_scores(id,title,category,difficulty,notation,credit,status,
                          created_at,artist,difficulty_stars,attributes)
SELECT 'upload-' || id::text,title,category,difficulty,'{}'::jsonb,
       '琴友 ' || substring(replace(user_id::text,'-','') from 1 for 6),'private',
       created_at,artist,difficulty_stars,attributes
FROM guitar_uploads WHERE app_id='guitar' AND status IN ('pending','rejected')
ON CONFLICT(app_id,id) DO NOTHING;

-- The active upload is the source of truth for its full description.
UPDATE guitar_scores s SET description=''
FROM guitar_uploads u
WHERE s.app_id=u.app_id AND s.id='upload-'||u.id::text AND s.status='private'
 AND u.status IN ('pending','rejected') AND s.description=u.description AND s.description<>'';

-- Historical deletes left identities without an upload. Only infer ownership
-- when all retained reading and practice records point to the same account.
WITH linked_accounts AS (
 SELECT app_id,score_id,user_id FROM guitar_reading_progress
 UNION
 SELECT app_id,score_id,user_id FROM guitar_practice_sessions
), known_owner AS (
 SELECT app_id,score_id,min(user_id::text)::uuid AS user_id
 FROM linked_accounts GROUP BY app_id,score_id HAVING count(*)=1
)
UPDATE guitar_scores s
SET status='archived',withdrawn_upload_owner_id=CASE WHEN s.id LIKE 'upload-%' THEN (
 SELECT o.user_id FROM known_owner o WHERE o.app_id=s.app_id AND o.score_id=s.id
) END
WHERE s.app_id='guitar' AND s.status='private' AND NOT EXISTS (
 SELECT 1 FROM guitar_uploads u WHERE u.app_id=s.app_id AND s.id='upload-'||u.id::text
 AND u.status IN ('pending','rejected')
);

CREATE FUNCTION archive_withdrawn_guitar_upload() RETURNS trigger
LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
BEGIN
 UPDATE guitar_scores
 SET status='archived',withdrawn_upload_owner_id=OLD.user_id,description=OLD.description
 WHERE app_id=OLD.app_id AND id='upload-'||OLD.id::text AND status='private';
 RETURN OLD;
END;
$$;

CREATE TRIGGER guitar_uploads_archive_withdrawn_score
BEFORE DELETE ON guitar_uploads FOR EACH ROW
WHEN (OLD.status IN ('pending','rejected'))
EXECUTE FUNCTION archive_withdrawn_guitar_upload();
