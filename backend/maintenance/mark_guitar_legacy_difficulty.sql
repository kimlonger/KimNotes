-- Feed CSV from the verified pre-031 backup on standard input. The columns
-- are app_id,id,difficulty,created_at,updated_at for rows with no saved stars.
-- Example: psql -X -v ON_ERROR_STOP=1 -f this_file.sql "$database" < legacy.csv
BEGIN;
SET LOCAL search_path TO kimcloud, public;
CREATE TEMP TABLE legacy_guitar_difficulty (
 app_id text NOT NULL,
 id text NOT NULL,
 difficulty text NOT NULL,
 created_at timestamptz NOT NULL,
 updated_at timestamptz NOT NULL,
 PRIMARY KEY(app_id,id)
) ON COMMIT DROP;
\copy legacy_guitar_difficulty FROM pstdin CSV

UPDATE guitar_scores s SET difficulty_source='legacy'
FROM legacy_guitar_difficulty previous
WHERE s.app_id=previous.app_id AND s.id=previous.id
 AND s.created_at=previous.created_at AND s.updated_at=previous.updated_at
 AND s.difficulty=previous.difficulty AND s.difficulty_source='unknown'
 AND s.difficulty_stars=CASE previous.difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END;
COMMIT;
