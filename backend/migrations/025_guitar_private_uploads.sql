-- Private score identities let practice progress survive approval without
-- copying the uploaded files into the public library before review.
ALTER TABLE guitar_scores DROP CONSTRAINT guitar_scores_status_check;
ALTER TABLE guitar_scores ADD CONSTRAINT guitar_scores_status_check
 CHECK(status IN ('published','archived','private'));

-- Existing pending and rejected uploads get the same private identity as new
-- uploads. The original files remain exclusively in guitar_uploads.
INSERT INTO guitar_scores(id,title,description,category,difficulty,notation,credit,
                          status,created_at,artist,genre,difficulty_stars,attributes)
SELECT 'upload-' || id::text,title,description,category,difficulty,'{}'::jsonb,
       '琴友 ' || substring(replace(user_id::text,'-','') from 1 for 6),
       'private',created_at,artist,genre,difficulty_stars,attributes
FROM guitar_uploads WHERE app_id='guitar' AND status IN ('pending','rejected')
ON CONFLICT(app_id,id) DO NOTHING;
