ALTER TABLE note_todos
  ADD COLUMN priority smallint NOT NULL DEFAULT 0 CHECK (priority BETWEEN 0 AND 2),
  ADD COLUMN notes text NOT NULL DEFAULT '' CHECK (octet_length(notes) <= 32768),
  ADD COLUMN pinned boolean NOT NULL DEFAULT false;
