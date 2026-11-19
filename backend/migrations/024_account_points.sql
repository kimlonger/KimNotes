CREATE TABLE account_point_wallets (
    user_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    balance integer NOT NULL DEFAULT 0 CHECK (balance >= 0),
    checked_in_on date,
    streak integer NOT NULL DEFAULT 0 CHECK (streak >= 0)
);

CREATE TABLE account_point_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    day date NOT NULL,
    app_id text NOT NULL CHECK (app_id IN ('main','kimnotes','guitar')),
    score_id text NOT NULL DEFAULT '',
    delta integer NOT NULL CHECK ((score_id = '' AND delta > 0) OR (score_id <> '' AND delta < 0)),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX account_points_one_checkin_per_day ON account_point_events(user_id, day) WHERE score_id = '';
CREATE INDEX account_point_events_owner_date ON account_point_events(user_id, day DESC);
