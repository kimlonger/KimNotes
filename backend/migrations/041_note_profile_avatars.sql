ALTER TABLE note_profiles
  ADD COLUMN avatar_png bytea,
  ADD COLUMN avatar_version uuid,
  ADD CONSTRAINT note_profiles_avatar_valid CHECK (
    (avatar_png IS NULL AND avatar_version IS NULL) OR
    (avatar_png IS NOT NULL AND avatar_version IS NOT NULL AND octet_length(avatar_png) BETWEEN 1 AND 524288)
  );

ALTER TABLE auth_challenges
  ADD COLUMN note_avatar_png bytea,
  ADD CONSTRAINT auth_challenges_note_avatar_valid CHECK (
    note_avatar_png IS NULL OR
    (app_id = 'kimnotes' AND purpose = 'register' AND octet_length(note_avatar_png) BETWEEN 1 AND 524288)
  );
