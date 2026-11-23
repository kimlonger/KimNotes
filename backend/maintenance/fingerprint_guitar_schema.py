"""Compare logical records across the 034 and 037 layouts without exporting user rows."""

from __future__ import annotations

import argparse
import json
import re
import subprocess


def fingerprint(database: str, new: bool) -> dict[str, tuple[int, str]]:
    if not re.fullmatch(r"kim_prod|kim_guitar_schema_(?:test|rollback|reference)_[0-9]+", database):
        raise ValueError("Unexpected database for schema verification")

    unchanged = (
        "users applications user_apps sessions access_tokens refresh_tokens web_cookie_aliases "
        "desktop_upgrades auth_challenges auth_events rate_limits account_point_wallets "
        "account_point_events main_account_entries main_account_follows main_account_totals "
        "content_articles content_updates content_works notes note_versions operations "
        "guitar_community_answers guitar_request_answers guitar_visits guitar_messages "
        "guitar_reading_progress guitar_practice_tasks guitar_practice_sessions"
    ).split()
    queries = {table: f"SELECT * FROM {table}" for table in unchanged}
    queries["attachments"] = (
        "SELECT to_jsonb(a)-'bytes' AS meta,encode(sha256(a.bytes),'hex') AS content FROM attachments a"
    )
    queries["guitar_artists"] = (
        "SELECT to_jsonb(a)-'avatar_bytes' AS meta,encode(sha256(a.avatar_bytes),'hex') AS photo FROM guitar_artists a"
    )
    for table in ("courses", "chapters", "lessons", "progress", "questions", "replies"):
        name = "guitar_" + table
        queries["archive_" + name] = f"SELECT * FROM kimcloud_archive.{name}"

    settings = "guitar_members" if new else "guitar_practice_preferences"
    queries["guitar_identity"] = """
        SELECT a.app_id,a.user_id,COALESCE(m.nickname,'琴友') AS nickname,
        COALESCE(m.avatar,'guitar') AS avatar,m.signature,m.avatar_mime,
        encode(sha256(m.avatar_bytes),'hex') AS image,COALESCE(m.updated_at,u.created_at) AS updated_at
        FROM user_apps a JOIN users u ON u.id=a.user_id
        LEFT JOIN guitar_members m USING (app_id,user_id) WHERE a.app_id='guitar'
    """
    queries["guitar_settings"] = f"""
        SELECT a.app_id,a.user_id,COALESCE(p.weekly_minutes,150) AS weekly_minutes,
        COALESCE(p.weekly_days,5) AS weekly_days,COALESCE(p.first_tuned,false) AS first_tuned,
        COALESCE(p.equipped_title,'') AS equipped_title
        FROM user_apps a LEFT JOIN {settings} p USING (app_id,user_id) WHERE a.app_id='guitar'
    """
    queries["guitar_practice_days"] = """
        SELECT app_id,user_id,COALESCE(practice_day,(started_at AT TIME ZONE 'Asia/Shanghai')::date) AS day,
        sum(seconds) AS seconds,count(*) AS records FROM guitar_practice_sessions
        WHERE ended_at IS NOT NULL AND seconds>0 GROUP BY app_id,user_id,day
    """
    desc = "s.description" if new else "COALESCE(u.description,s.description)"
    notation = "CASE WHEN s.status='private' THEN NULL ELSE s.notation END"
    if new:
        notation = "CASE WHEN s.file_format='none' THEN '{}'::jsonb ELSE s.notation END"
    join = "" if new else (
        "LEFT JOIN guitar_uploads u ON u.app_id=s.app_id AND 'upload-'||u.id::text=s.id "
        "AND s.status='private' AND u.status IN ('pending','rejected')"
    )
    queries["guitar_scores"] = f"""
        SELECT s.app_id,s.id,s.title,{desc} AS description,s.category,s.difficulty,
        {notation} AS notation,s.credit,s.status,s.created_at,s.artist,s.genre,s.updated_at,
        s.difficulty_stars,s.artist_id,s.attributes,s.withdrawn_upload_owner_id
        FROM guitar_scores s {join}
    """
    fields = ("title", "description", "category", "difficulty", "artist", "genre", "difficulty_stars", "attributes")
    if new:
        public = "CASE WHEN u.status='approved' THEN u.score_id END"
        metadata = "COALESCE(u.original_metadata,jsonb_build_object(" + ",".join(f"'{f}',s.{f}" for f in fields) + "))"
        parent = "JOIN guitar_scores s ON (s.app_id,s.id)=(u.app_id,u.score_id)"
        queries["guitar_catalog_media"] = """
            SELECT f.app_id,f.score_id,f.file_no,f.mime,encode(sha256(f.bytes),'hex') AS content
            FROM guitar_score_files f JOIN guitar_scores s ON (s.app_id,s.id)=(f.app_id,f.score_id)
            WHERE f.original_upload_id IS NULL AND s.status<>'private'
        """
        queries["guitar_upload_media"] = """
            SELECT u.app_id,u.id AS upload_id,f.file_no,f.mime,encode(sha256(f.bytes),'hex') AS content
            FROM guitar_uploads u JOIN guitar_score_files f ON (f.app_id,f.score_id)=(u.app_id,u.score_id)
            WHERE f.original_upload_id IS NOT DISTINCT FROM
             (SELECT original_upload_id FROM guitar_score_files o WHERE (o.app_id,o.original_upload_id)=(u.app_id,u.id) LIMIT 1)
        """
    else:
        public = "u.published_score_id"
        metadata = "jsonb_build_object(" + ",".join(f"'{f}',u.{f}" for f in fields) + ")"
        parent = ""
        queries["guitar_catalog_media"] = """
            SELECT app_id,id AS score_id,1 AS file_no,mime,encode(sha256(bytes),'hex') AS content
            FROM guitar_scores WHERE bytes IS NOT NULL
            UNION ALL SELECT app_id,score_id,page,mime,encode(sha256(bytes),'hex') FROM guitar_score_pages WHERE score_id IS NOT NULL
        """
        queries["guitar_upload_media"] = """
            SELECT app_id,id AS upload_id,1 AS file_no,mime,encode(sha256(bytes),'hex') AS content FROM guitar_uploads
            UNION ALL SELECT app_id,upload_id,page,mime,encode(sha256(bytes),'hex') FROM guitar_score_pages WHERE upload_id IS NOT NULL
        """
    queries["guitar_uploads"] = f"""
        SELECT u.app_id,u.id,u.user_id,u.filename,u.status,u.review_note,{public} AS published_score_id,
        u.created_at,u.permission_confirmed_at,u.reviewed_at,u.request_id,{metadata} AS metadata
        FROM guitar_uploads u {parent}
    """

    union = " UNION ALL ".join(
        f"SELECT '{name}',count(*),encode(sha256(convert_to(COALESCE(string_agg(d,'' ORDER BY d),''),'UTF8')),'hex') "
        f"FROM (SELECT encode(sha256(convert_to(to_jsonb(r)::text,'UTF8')),'hex') AS d FROM ({query}) r) fingerprints"
        for name, query in queries.items()
    )
    sql = f"BEGIN READ ONLY; SET LOCAL search_path TO kimcloud,public; SET LOCAL TIME ZONE 'UTC'; {union}; COMMIT;"
    output = subprocess.check_output(
        ["sudo", "-n", "-u", "postgres", "psql", "-XqAt", "-v", "ON_ERROR_STOP=1", "-d", database, "-c", sql],
        text=True,
    )
    return {name: (int(count), digest) for name, count, digest in (line.split("|") for line in output.splitlines())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database")
    parser.add_argument("layout", choices=("034", "037"))
    args = parser.parse_args()
    print(json.dumps(fingerprint(args.database, args.layout == "037"), indent=2, sort_keys=True))
