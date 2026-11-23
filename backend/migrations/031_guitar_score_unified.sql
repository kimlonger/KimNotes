-- A member's favorite and reading state share one stable score identity.
ALTER TABLE guitar_reading_progress
 ADD COLUMN favorite_at timestamptz,
 ADD COLUMN reading_started boolean NOT NULL DEFAULT true;

INSERT INTO guitar_reading_progress(app_id,user_id,score_id,favorite_at,reading_started)
SELECT f.app_id,f.user_id,f.target_id,f.created_at,false
FROM guitar_favorites f JOIN guitar_scores s ON s.app_id=f.app_id AND s.id=f.target_id
WHERE f.kind='score'
ON CONFLICT(app_id,user_id,score_id) DO UPDATE SET favorite_at=EXCLUDED.favorite_at;

CREATE INDEX guitar_reading_favorites_owner ON guitar_reading_progress(app_id,user_id,favorite_at DESC,score_id)
 WHERE favorite_at IS NOT NULL;
CREATE INDEX guitar_reading_favorites_score ON guitar_reading_progress(app_id,score_id,favorite_at DESC)
 WHERE favorite_at IS NOT NULL;

-- Retired course/lesson favorites and dangling legacy targets remain archival
-- data. Fresh installations do not create an empty archival table.
DO $$
DECLARE
 archive_schema text := current_schema() || '_archive';
BEGIN
 IF EXISTS(SELECT 1 FROM guitar_favorites f WHERE f.kind<>'score' OR NOT EXISTS(
   SELECT 1 FROM guitar_scores s WHERE s.app_id=f.app_id AND s.id=f.target_id)) THEN
  EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I',archive_schema);
  EXECUTE format('CREATE TABLE %I.guitar_legacy_favorites (LIKE guitar_favorites INCLUDING ALL)',archive_schema);
  EXECUTE format('ALTER TABLE %I.guitar_legacy_favorites ADD FOREIGN KEY(app_id,user_id) REFERENCES %I.user_apps(app_id,user_id) ON DELETE CASCADE',archive_schema,current_schema());
  EXECUTE format('INSERT INTO %I.guitar_legacy_favorites SELECT f.* FROM guitar_favorites f WHERE f.kind<>''score'' OR NOT EXISTS(SELECT 1 FROM guitar_scores s WHERE s.app_id=f.app_id AND s.id=f.target_id)',archive_schema);
 END IF;
END;
$$;
DROP TABLE guitar_favorites;

-- Uploaded originals and public pages retain independent owners and lifetimes
-- in one table. An approved submission can therefore still be inspected or
-- deleted without modifying the published arrangement.
ALTER TABLE guitar_score_pages DROP CONSTRAINT guitar_score_pages_pkey;
ALTER TABLE guitar_score_pages ALTER COLUMN score_id DROP NOT NULL;
ALTER TABLE guitar_score_pages
 ADD COLUMN id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 ADD COLUMN upload_id uuid,
 ADD CONSTRAINT guitar_score_pages_owner CHECK ((score_id IS NULL) <> (upload_id IS NULL)),
 ADD CONSTRAINT guitar_score_pages_score_unique UNIQUE(app_id,score_id,page),
 ADD CONSTRAINT guitar_score_pages_upload_unique UNIQUE(app_id,upload_id,page),
 ADD CONSTRAINT guitar_score_pages_upload_ref FOREIGN KEY(app_id,upload_id)
  REFERENCES guitar_uploads(app_id,id) ON DELETE CASCADE;

INSERT INTO guitar_score_pages(app_id,upload_id,page,mime,bytes)
SELECT app_id,upload_id,page,mime,bytes FROM guitar_upload_pages;
DROP TABLE guitar_upload_pages;

-- Resolve metadata once when it is written. Explicit metadata wins over the
-- structured notation, followed by narrowly recognized legacy descriptions.
CREATE FUNCTION guitar_resolved_metadata(attributes jsonb,notation jsonb,description text)
RETURNS jsonb LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
 result jsonb := COALESCE(attributes,'{}'::jsonb);
 field text;
 value jsonb;
 matched text[];
BEGIN
 FOREACH field IN ARRAY ARRAY['key','capo','meter','bpm','chords'] LOOP
  IF result ? field AND result->field NOT IN ('null'::jsonb,'""'::jsonb) THEN
   CONTINUE;
  END IF;
  value := notation->field;
  IF value IS NULL OR value IN ('null'::jsonb,'""'::jsonb) THEN
   value := NULL;
   IF field='key' THEN
    value := to_jsonb(substring(description from '([A-G][#b]?)调'));
   ELSIF field='bpm' THEN
    value := to_jsonb(substring(description from '([0-9]{2,3})[[:space:]]*[Bb][Pp][Mm]')::integer);
   ELSIF field='capo' THEN
    matched := regexp_match(description,'变调夹[[:space:]]*([0-9]{1,2})([^0-9]|$)');
    IF matched IS NOT NULL AND matched[1]::integer<=12 THEN
     value := to_jsonb(matched[1]::integer);
    END IF;
   ELSIF field='meter' THEN
    matched := regexp_match(description,'\m([2-9]|1[0-2])/(2|4|8|16)\M');
    IF matched IS NOT NULL THEN
     value := jsonb_build_array(matched[1]::integer,matched[2]::integer);
    END IF;
   END IF;
  END IF;
  IF value IS NOT NULL THEN
   result := jsonb_set(result,ARRAY[field],value);
  END IF;
 END LOOP;
 RETURN result;
END;
$$;

CREATE FUNCTION guitar_normalize_score_metadata() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
DECLARE
 inherited jsonb;
 field text;
BEGIN
 IF TG_OP='UPDATE' AND NEW.attributes IS NOT DISTINCT FROM OLD.attributes
  AND (NEW.notation IS DISTINCT FROM OLD.notation OR NEW.description IS DISTINCT FROM OLD.description) THEN
  inherited := guitar_resolved_metadata('{}',OLD.notation,OLD.description);
  FOREACH field IN ARRAY ARRAY['key','capo','meter','bpm','chords'] LOOP
   IF OLD.attributes->field=inherited->field THEN
    NEW.attributes := NEW.attributes-field;
   END IF;
  END LOOP;
 END IF;
 NEW.attributes := guitar_resolved_metadata(NEW.attributes,NEW.notation,NEW.description);
 NEW.difficulty_stars := COALESCE(NEW.difficulty_stars,
  CASE NEW.difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END);
 RETURN NEW;
END;
$$;
CREATE TRIGGER guitar_scores_normalize_metadata
BEFORE INSERT OR UPDATE OF attributes,notation,description,difficulty,difficulty_stars ON guitar_scores
FOR EACH ROW EXECUTE FUNCTION guitar_normalize_score_metadata();

CREATE FUNCTION guitar_normalize_upload_metadata() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
DECLARE
 inherited jsonb;
 field text;
BEGIN
 IF TG_OP='UPDATE' AND NEW.attributes IS NOT DISTINCT FROM OLD.attributes
  AND NEW.description IS DISTINCT FROM OLD.description THEN
  inherited := guitar_resolved_metadata('{}',NULL,OLD.description);
  FOREACH field IN ARRAY ARRAY['key','capo','meter','bpm','chords'] LOOP
   IF OLD.attributes->field=inherited->field THEN
    NEW.attributes := NEW.attributes-field;
   END IF;
  END LOOP;
 END IF;
 NEW.attributes := guitar_resolved_metadata(NEW.attributes,NULL,NEW.description);
 NEW.difficulty_stars := COALESCE(NEW.difficulty_stars,
  CASE NEW.difficulty WHEN '入门' THEN 1 WHEN '初级' THEN 2 ELSE 3 END);
 RETURN NEW;
END;
$$;
CREATE TRIGGER guitar_uploads_normalize_metadata
BEFORE INSERT OR UPDATE OF attributes,description,difficulty,difficulty_stars ON guitar_uploads
FOR EACH ROW EXECUTE FUNCTION guitar_normalize_upload_metadata();

UPDATE guitar_uploads SET attributes=attributes;
UPDATE guitar_scores s SET attributes=guitar_resolved_metadata(u.attributes||s.attributes,s.notation,u.description)
FROM guitar_uploads u WHERE u.app_id=s.app_id AND s.id='upload-'||u.id::text
 AND s.status='private' AND u.status IN ('pending','rejected');
UPDATE guitar_scores SET attributes=attributes;

-- Canonical request notes no longer duplicate their generated title/category
-- lines. Exact prefixes from old clients are migrated once and still accepted
-- by the application at its input boundary.
WITH legacy AS (
 SELECT id,app_id,
  CASE WHEN left(split_part(body,chr(10),1),char_length('求谱：《'||requested_title||'》'))='求谱：《'||requested_title||'》'
   THEN CASE WHEN strpos(body,chr(10))>0 THEN substring(body FROM strpos(body,chr(10))+1) ELSE '' END
   ELSE body END AS note
 FROM guitar_messages WHERE requested_title IS NOT NULL
), parsed AS (
 SELECT *,CASE WHEN split_part(note,chr(10),1) IN ('类型：弹唱','类型：指弹')
  THEN substring(split_part(note,chr(10),1) FROM 4) END AS category
 FROM legacy
)
UPDATE guitar_messages m SET
 requested_category=COALESCE(NULLIF(m.requested_category,''),p.category),
 body=CASE WHEN p.category IS NULL THEN p.note
  WHEN strpos(p.note,chr(10))>0 THEN substring(p.note FROM strpos(p.note,chr(10))+1) ELSE '' END
FROM parsed p WHERE m.app_id=p.app_id AND m.id=p.id;
