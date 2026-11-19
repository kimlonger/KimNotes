-- Run with psql -X -v ON_ERROR_STOP=1 while the backend is stopped.
-- This restores the old structure from all current rows, including writes
-- that committed after compaction. Pause writes until the old binary is ready.
BEGIN;
SELECT pg_advisory_xact_lock(725194138);
SET LOCAL search_path TO kimcloud, public;
LOCK TABLE operations, note_versions IN ACCESS EXCLUSIVE MODE;

CREATE TABLE note_changes (
  app_id text NOT NULL, user_id uuid NOT NULL, sequence bigint NOT NULL,
  note_id uuid NOT NULL, revision bigint NOT NULL,
  PRIMARY KEY(app_id,user_id,sequence),
  FOREIGN KEY(app_id,user_id,note_id,revision) REFERENCES note_versions(app_id,user_id,note_id,revision)
);
INSERT INTO note_changes(app_id,user_id,sequence,note_id,revision)
SELECT app_id,user_id,sequence,note_id,revision FROM note_versions;

ALTER TABLE operations DROP CONSTRAINT operations_result_version_check;
UPDATE operations o SET response=o.response || jsonb_build_object('note',v.snapshot)
FROM note_versions v
WHERE (v.app_id,v.user_id,v.note_id,v.revision)=(o.app_id,o.user_id,o.result_note_id,o.result_revision);
ALTER TABLE operations DROP CONSTRAINT operations_result_version_fkey,
  DROP COLUMN result_note_id, DROP COLUMN result_revision;
ALTER TABLE note_versions DROP CONSTRAINT note_versions_stream_key, DROP COLUMN sequence;
DELETE FROM schema_migrations WHERE version='027_note_sync_compaction';
COMMIT;
