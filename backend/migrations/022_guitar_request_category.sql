-- A NULL value means that no structured type was supplied by the author.
ALTER TABLE guitar_messages ADD COLUMN requested_category text;
ALTER TABLE guitar_messages ADD CONSTRAINT guitar_request_category_valid
 CHECK (requested_category IS NULL OR requested_category IN ('弹唱', '指弹'));
