CREATE TABLE note_pickup_codes (
    id uuid PRIMARY KEY,
    code_key bytea NOT NULL UNIQUE CHECK (octet_length(code_key) = 32),
    code_attempt smallint NOT NULL CHECK (code_attempt BETWEEN 0 AND 7),
    sender_session_id uuid NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    sender_public_key bytea NOT NULL CHECK (octet_length(sender_public_key) = 64),
    expires_at timestamptz NOT NULL DEFAULT now() + interval '5 minutes',
    receiver_session_id uuid REFERENCES sessions(id) ON DELETE CASCADE,
    receiver_public_key bytea CHECK (octet_length(receiver_public_key) = 64),
    claimed_at timestamptz,
    canceled_at timestamptz,
    CHECK (
        (receiver_session_id IS NULL AND receiver_public_key IS NULL AND claimed_at IS NULL) OR
        (receiver_session_id IS NOT NULL AND receiver_session_id <> sender_session_id
         AND receiver_public_key IS NOT NULL AND claimed_at IS NOT NULL)
    )
);

CREATE INDEX note_pickup_codes_sender_pending ON note_pickup_codes(sender_session_id, expires_at)
    WHERE claimed_at IS NULL AND canceled_at IS NULL;
CREATE INDEX note_pickup_codes_expiry ON note_pickup_codes(expires_at);
