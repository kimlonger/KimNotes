CREATE TABLE note_browser_handoffs (
    code_hash text PRIMARY KEY,
    challenge bytea NOT NULL CHECK (octet_length(challenge) = 32),
    source_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    source_session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE INDEX note_browser_handoffs_expiry ON note_browser_handoffs(expires_at);
CREATE INDEX note_browser_handoffs_session ON note_browser_handoffs(source_session_id);
