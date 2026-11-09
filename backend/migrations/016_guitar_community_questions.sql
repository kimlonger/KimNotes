ALTER TABLE guitar_messages ADD COLUMN question_title text;
ALTER TABLE guitar_messages ADD CONSTRAINT guitar_message_one_topic CHECK (requested_title IS NULL OR question_title IS NULL);

CREATE TABLE guitar_community_answers (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 id uuid NOT NULL,
 question_id uuid NOT NULL,
 author_id uuid REFERENCES users(id) ON DELETE SET NULL,
 body text NOT NULL CHECK(char_length(body) BETWEEN 2 AND 2000),
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(app_id,id),
 FOREIGN KEY(app_id,question_id) REFERENCES guitar_messages(app_id,id) ON DELETE CASCADE
);
CREATE INDEX guitar_community_answers_question ON guitar_community_answers(app_id,question_id,created_at,id);
