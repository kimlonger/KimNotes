-- A CHECK accepts SQL NULL. Explicitly require both fields for custom avatars
-- and file-backed scores so an incomplete pair cannot pass validation.
ALTER TABLE guitar_members
 ADD CONSTRAINT guitar_members_avatar_media_valid CHECK (
  (avatar_bytes IS NULL AND avatar_mime IS NULL)
  OR (avatar_bytes IS NOT NULL AND avatar_mime IS NOT NULL
      AND octet_length(avatar_bytes) BETWEEN 1 AND 524288
      AND avatar_mime IN ('image/png','image/jpeg','image/webp'))
 );

ALTER TABLE guitar_scores
 ADD CONSTRAINT guitar_scores_file_media_valid CHECK (
  (bytes IS NULL AND mime IS NULL)
  OR (bytes IS NOT NULL AND mime IS NOT NULL AND octet_length(bytes) > 0
      AND mime IN ('image/png','image/jpeg','image/webp','application/pdf'))
 );
