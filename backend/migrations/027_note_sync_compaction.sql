-- Preserve the existing per-user sync stream while storing its cursor on the
-- immutable version itself. Stop if the old schema ever used a version twice.
LOCK TABLE operations, note_versions, note_changes IN ACCESS EXCLUSIVE MODE;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM note_changes
    GROUP BY app_id, user_id, note_id, revision HAVING count(*) > 1
  ) THEN
    RAISE EXCEPTION 'note sync compaction requires one change per version';
  END IF;
END $$;

ALTER TABLE note_versions ADD COLUMN sequence bigint;
UPDATE note_versions v SET sequence=c.sequence
FROM note_changes c
WHERE (c.app_id,c.user_id,c.note_id,c.revision)=(v.app_id,v.user_id,v.note_id,v.revision);
ALTER TABLE note_versions ALTER COLUMN sequence SET NOT NULL;
ALTER TABLE note_versions ADD CONSTRAINT note_versions_stream_key UNIQUE (app_id,user_id,sequence);

ALTER TABLE operations
  ADD COLUMN result_note_id uuid,
  ADD COLUMN result_revision bigint,
  ADD CONSTRAINT operations_result_version_fkey FOREIGN KEY (app_id,user_id,result_note_id,result_revision)
    REFERENCES note_versions(app_id,user_id,note_id,revision),
  ADD CONSTRAINT operations_result_version_check CHECK (
    (result_note_id IS NULL AND result_revision IS NULL) OR
    (result_note_id IS NOT NULL AND result_revision IS NOT NULL AND jsonb_typeof(response)='object' AND NOT (response ? 'note'))
  );

-- Only remove a saved document when the version can reconstruct it exactly.
-- Earlier versions or imported responses with different JSON stay untouched.
UPDATE operations o
SET result_note_id=v.note_id, result_revision=v.revision, response=o.response-'note'
FROM note_versions v
WHERE o.app_id=v.app_id AND o.user_id=v.user_id
  AND o.response->'note'->>'id'=v.note_id::text
  AND o.response->'note'->>'revision'=v.revision::text
  AND (o.response->'note')::text=v.snapshot::text;

DROP TABLE note_changes;
