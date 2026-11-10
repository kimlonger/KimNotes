-- Use the fingering key shown in the imported arrangement. The two versions
-- of Alaska Bay share one score record, so the first page supplies the key.
UPDATE guitar_scores AS score
SET attributes = reviewed.attributes || score.attributes
FROM (VALUES
  ('provided-tabs-18c203f4bd570c2d3a3eea5c', '{"key":"C","capo":1}'::jsonb),
  ('provided-tabs-39d6da4bacb373fbe13d3d9e', '{"key":"G","capo":3}'::jsonb),
  ('provided-tabs-0e94a87b260a06b1c0456005', '{"key":"C","bpm":70}'::jsonb)
) AS reviewed(id, attributes)
WHERE score.app_id = 'guitar'
  AND score.id = reviewed.id
  AND score.attributes IS DISTINCT FROM reviewed.attributes || score.attributes;
