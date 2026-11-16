-- NULL means that no shared signature has been set or migrated yet.  An
-- empty string is an explicit user choice and must not be overwritten by a
-- stale value migrated from a device.
ALTER TABLE guitar_members ADD COLUMN signature text;
ALTER TABLE guitar_members ADD CONSTRAINT guitar_signature_length CHECK (signature IS NULL OR char_length(signature) <= 60);
