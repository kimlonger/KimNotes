ALTER TABLE guitar_practice_preferences
 ADD COLUMN equipped_title text NOT NULL DEFAULT ''
 CHECK (equipped_title = '' OR equipped_title ~ '^(songs|time)-[1-7]$');
