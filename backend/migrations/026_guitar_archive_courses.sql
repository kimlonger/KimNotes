-- Preserve the retired course data and its foreign keys outside the application search path.
CREATE SCHEMA IF NOT EXISTS kimcloud_archive;

ALTER TABLE kimcloud.guitar_courses SET SCHEMA kimcloud_archive;
ALTER TABLE kimcloud.guitar_chapters SET SCHEMA kimcloud_archive;
ALTER TABLE kimcloud.guitar_lessons SET SCHEMA kimcloud_archive;
ALTER TABLE kimcloud.guitar_progress SET SCHEMA kimcloud_archive;
ALTER TABLE kimcloud.guitar_questions SET SCHEMA kimcloud_archive;
ALTER TABLE kimcloud.guitar_replies SET SCHEMA kimcloud_archive;
