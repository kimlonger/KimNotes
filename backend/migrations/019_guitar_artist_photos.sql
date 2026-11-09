ALTER TABLE guitar_artists
 ADD COLUMN avatar_bytes bytea,
 ADD COLUMN avatar_mime text,
 ADD COLUMN avatar_updated_at timestamptz,
 ADD CONSTRAINT guitar_artist_avatar_valid CHECK (
  (avatar_bytes IS NULL AND avatar_mime IS NULL AND avatar_updated_at IS NULL)
  OR (avatar_bytes IS NOT NULL AND avatar_mime IS NOT NULL AND avatar_updated_at IS NOT NULL
      AND avatar_mime IN ('image/png','image/jpeg','image/webp')
      AND octet_length(avatar_bytes) BETWEEN 1 AND 5242880)
 );
