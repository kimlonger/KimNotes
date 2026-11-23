ALTER TABLE guitar_members
 ADD COLUMN weekly_minutes integer NOT NULL DEFAULT 150 CHECK(weekly_minutes BETWEEN 100 AND 600),
 ADD COLUMN weekly_days integer NOT NULL DEFAULT 5 CHECK(weekly_days BETWEEN 1 AND 7),
 ADD COLUMN first_tuned boolean NOT NULL DEFAULT false,
 ADD COLUMN equipped_title text NOT NULL DEFAULT '' CHECK(equipped_title='' OR equipped_title ~ '^(songs|time)-[1-7]$');

INSERT INTO guitar_members(app_id,user_id,weekly_minutes,weekly_days,first_tuned,equipped_title,updated_at)
SELECT p.app_id,p.user_id,p.weekly_minutes,p.weekly_days,p.first_tuned,p.equipped_title,u.created_at
FROM guitar_practice_preferences p JOIN users u ON u.id=p.user_id
ON CONFLICT(app_id,user_id) DO UPDATE SET weekly_minutes=EXCLUDED.weekly_minutes,
 weekly_days=EXCLUDED.weekly_days,first_tuned=EXCLUDED.first_tuned,equipped_title=EXCLUDED.equipped_title;

DROP TABLE guitar_practice_preferences;

-- Existing ratings are unattributed until compared with the pre-031 backup.
-- A legacy estimate is kept separate from the numeric value older clients use.
ALTER TABLE guitar_scores ADD COLUMN IF NOT EXISTS difficulty_source text NOT NULL DEFAULT 'unknown'
 CHECK(difficulty_source IN ('unknown','legacy','submitted','verified'));
