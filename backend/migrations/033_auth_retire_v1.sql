-- The expired first-generation desktop password/token exchange is retired.
-- Abort if there is still a usable v1 desktop credential in an open window.
DO $$
BEGIN
 IF EXISTS (
   SELECT 1 FROM sessions s JOIN users u ON u.id=s.user_id
   WHERE s.client_kind='desktop' AND s.protocol_version=1
     AND s.revoked_at IS NULL AND s.expires_at>now() AND s.auth_version=u.auth_version
     AND EXISTS (SELECT 1 FROM auth_policy WHERE id=1 AND legacy_upgrade_until>now())
 ) THEN
   RAISE EXCEPTION 'legacy desktop window has a live session';
 END IF;
END $$;

-- The existing identity index already backs the foreign keys of both token
-- families and the retry caches. Make it the primary key without replacing it.
ALTER TABLE sessions DROP CONSTRAINT sessions_pkey;
ALTER TABLE sessions ALTER COLUMN token_hash DROP NOT NULL;
ALTER TABLE sessions ADD CONSTRAINT sessions_pkey PRIMARY KEY USING INDEX sessions_identity;
UPDATE sessions SET token_hash=NULL WHERE client_kind='desktop';
ALTER TABLE sessions ADD CONSTRAINT sessions_web_cookie_hash_check
  CHECK ((client_kind='web') = (token_hash IS NOT NULL));
CREATE UNIQUE INDEX sessions_web_cookie_hash_key ON sessions(token_hash) WHERE token_hash IS NOT NULL;

ALTER TABLE refresh_tokens DROP COLUMN replaced_by;
DROP TABLE auth_policy;
