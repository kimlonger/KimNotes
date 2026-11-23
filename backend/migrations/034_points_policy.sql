ALTER TABLE account_point_events ADD COLUMN event_kind text;
UPDATE account_point_events SET event_kind=CASE WHEN score_id='' THEN 'check_in' ELSE 'score_download' END;
ALTER TABLE account_point_events ALTER COLUMN event_kind SET NOT NULL;

-- Historical and installed-client downloads have always debited the guitar
-- policy. Preserve ledger rows and express that scope directly in the schema.
ALTER TABLE account_point_events ADD CONSTRAINT account_point_events_kind_check CHECK (
  (event_kind='check_in' AND score_id='' AND delta>0)
  OR (event_kind='score_download' AND app_id='guitar' AND score_id<>'' AND delta<0)
);
ALTER TABLE account_point_events DROP CONSTRAINT account_point_events_check;
DROP INDEX account_points_one_checkin_per_day;
CREATE UNIQUE INDEX account_points_one_checkin_per_day
  ON account_point_events(user_id,day) WHERE event_kind='check_in';
