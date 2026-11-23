-- Acceptance/rollback helper. Stop the backend and pause writes until an older
-- binary is ready. Restore the document from each current revision, including
-- writes committed after migration 032. Does not rewrite snapshots or receipts.
BEGIN;
SELECT pg_advisory_xact_lock(725194138);
SET LOCAL search_path TO kimcloud, public;
LOCK TABLE notes, note_versions IN ACCESS EXCLUSIVE MODE;

ALTER TABLE notes ADD COLUMN document jsonb;
UPDATE notes n SET document=v.snapshot->'document'
FROM note_versions v
WHERE (v.app_id,v.user_id,v.note_id,v.revision)=(n.app_id,n.user_id,n.id,n.revision);
ALTER TABLE notes ALTER COLUMN document SET NOT NULL;
ALTER TABLE notes DROP CONSTRAINT notes_current_version;
DELETE FROM schema_migrations WHERE version='032_note_canonical_document';
COMMIT;
