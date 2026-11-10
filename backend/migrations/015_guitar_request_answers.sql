ALTER TABLE guitar_uploads ADD COLUMN request_id uuid;
ALTER TABLE guitar_uploads ADD CONSTRAINT guitar_uploads_request
 FOREIGN KEY(app_id,request_id) REFERENCES guitar_messages(app_id,id) ON DELETE SET NULL (request_id);

CREATE TABLE guitar_request_answers (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),
 request_id uuid NOT NULL,
 score_id text NOT NULL,
 responder_id uuid REFERENCES users(id) ON DELETE SET NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 accepted_at timestamptz,
 PRIMARY KEY(app_id,request_id,score_id),
 FOREIGN KEY(app_id,request_id) REFERENCES guitar_messages(app_id,id) ON DELETE CASCADE,
 FOREIGN KEY(app_id,score_id) REFERENCES guitar_scores(app_id,id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX guitar_request_one_solution ON guitar_request_answers(app_id,request_id) WHERE accepted_at IS NOT NULL;
