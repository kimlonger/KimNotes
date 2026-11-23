-- A note head keeps indexed metadata and a revision pointer. Its immutable
-- version is the only stored document. Refuse ambiguous legacy data rather
-- than choosing a body or silently rewriting history.
LOCK TABLE notes, note_versions IN ACCESS EXCLUSIVE MODE;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM notes n LEFT JOIN note_versions v
    ON (v.app_id,v.user_id,v.note_id,v.revision)=(n.app_id,n.user_id,n.id,n.revision)
    WHERE v.note_id IS NULL
       OR jsonb_typeof(v.snapshot->'document') IS DISTINCT FROM 'object'
       OR n.document IS DISTINCT FROM v.snapshot->'document'
       OR EXISTS (
         SELECT 1 FROM note_versions newer
         WHERE (newer.app_id,newer.user_id,newer.note_id)=(n.app_id,n.user_id,n.id)
           AND newer.revision>n.revision
       )
  ) THEN
    RAISE EXCEPTION 'note canonical document requires every head to match its latest version document';
  END IF;
END $$;

-- The head is updated before its version is appended in the same transaction.
-- Defer this reverse FK until commit; the existing version-to-head FK remains.
ALTER TABLE notes ADD CONSTRAINT notes_current_version
  FOREIGN KEY (app_id,user_id,id,revision)
  REFERENCES note_versions(app_id,user_id,note_id,revision)
  DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE notes DROP COLUMN document;
