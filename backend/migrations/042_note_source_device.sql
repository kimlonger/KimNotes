ALTER TABLE notes
  ADD COLUMN source_device_name text,
  ADD COLUMN source_device_kind text,
  ADD CONSTRAINT notes_source_device_valid CHECK (
    (source_device_name IS NULL AND source_device_kind IS NULL) OR
    (app_id = 'kimnotes' AND source_device_name IS NOT NULL AND source_device_kind IS NOT NULL
      AND char_length(source_device_name) BETWEEN 1 AND 120
      AND source_device_kind IN ('desktop', 'web'))
  );

COMMENT ON COLUMN notes.source_device_name IS '首次上传便签时的登录设备名称';
COMMENT ON COLUMN notes.source_device_kind IS '首次上传便签时的登录设备类型';
