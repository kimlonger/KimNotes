-- Page one remains in the original record for legacy clients. Additional pages
-- are copied on approval and never depend on a personal upload afterwards.
CREATE TABLE guitar_upload_pages (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),upload_id uuid NOT NULL,
 page integer NOT NULL CHECK(page BETWEEN 2 AND 12),mime text NOT NULL CHECK(mime IN ('image/png','image/jpeg','image/webp')),bytes bytea NOT NULL,
 PRIMARY KEY(app_id,upload_id,page),FOREIGN KEY(app_id,upload_id) REFERENCES guitar_uploads(app_id,id) ON DELETE CASCADE
);
CREATE TABLE guitar_score_pages (
 app_id text NOT NULL DEFAULT 'guitar' CHECK(app_id='guitar'),score_id text NOT NULL,
 page integer NOT NULL CHECK(page BETWEEN 2 AND 12),mime text NOT NULL CHECK(mime IN ('image/png','image/jpeg','image/webp')),bytes bytea NOT NULL,
 PRIMARY KEY(app_id,score_id,page),FOREIGN KEY(app_id,score_id) REFERENCES guitar_scores(app_id,id) ON DELETE CASCADE
);
