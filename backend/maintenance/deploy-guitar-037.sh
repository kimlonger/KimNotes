#!/usr/bin/env bash
set -euo pipefail
umask 077

stage=$(realpath -- "${1:?verified staging directory required}")
case "$stage" in /var/tmp/kim-guitar-schema-next.*) ;; *) exit 2 ;; esac
test "$(id -u)" -eq 0
root=/opt/kim-note-guitar-production-20261116
api=kim-note-guitar-api-20261116.service
gateway=kim-note-guitar-gateway-20261116.service
binary="$root/backend/kim-api"
index="$root/guitar/dist/client/index.html"
candidate="$stage/kim-api.037"
dsn='postgresql:///kim_prod?host=/var/run/postgresql'
old_build=7f17ef31bca28b7999f1754f87b1a6435fef8ce11aaf9c45376b42de1ef9d0b2

sql() { runuser -u postgres -- psql -XqAt -v ON_ERROR_STOP=1 -d kim_prod "$@"; }
version() { sql -c 'SELECT max(left(version,3)) FROM kimcloud.schema_migrations'; }
ready() {
  for ((attempt=1; attempt<=24; attempt++)); do
    if curl --noproxy '*' --max-time 2 -fsS http://127.0.0.1:19180/readyz >/dev/null 2>&1; then
      return
    fi
    sleep 0.5
  done
  return 1
}
install_binary() {
  install -o root -g root -m 755 "$1" "$binary.schema-037"
  mv -fT "$binary.schema-037" "$binary"
}

test "$(version)" = 034
test -f "$stage/rehearsal-verified"
(cd "$stage" && sha256sum --strict -c RELEASE.SHA256)
systemctl is-active --quiet "$api"
systemctl is-active --quiet "$gateway"
pid=$(systemctl show -p MainPID --value "$api")
test "$(readlink -f "/proc/$pid/exe")" = "$binary"
python3 - "$pid" <<'PY'
from pathlib import Path
import sys
from urllib.parse import urlsplit
values = dict(part.split("=", 1) for part in Path(f"/proc/{sys.argv[1]}/environ").read_bytes().decode().split("\0") if "=" in part)
connection = urlsplit(values["DATABASE_URL"])
assert connection.path == "/kim_prod" and connection.hostname in {"localhost", "127.0.0.1"}
assert values.get("LISTEN_ADDR") == "127.0.0.1:19180"
PY
printf '%s  %s\n' "$old_build" "$binary" | sha256sum -c -
printf '%s  %s\n' "$old_build" "/proc/$pid/exe" | sha256sum -c -
python3 "$stage/install-practice-data-release.py" check "$stage"

backup=$(mktemp -d /var/backups/kim-guitar-prod-before-037.XXXXXXXX)
printf 'CUTOVER_BACKUP=%s\n' "$backup"
cp -a "$binary" "$backup/kim-api.034"
cp -a "$index" "$backup/guitar-index.034.html"
cp "$stage"/rollback_037_to_034.sql "$stage"/fingerprint_guitar_schema.py "$stage"/RELEASE.SHA256 "$backup/"

frozen=0
recover() {
  outcome=$?
  trap - EXIT
  if (( outcome != 0 && frozen == 1 )) && [[ ! -e "$backup/traffic-reopened" ]]; then
    systemctl stop "$gateway" "$api" || exit "$outcome"
    db_version=$(version) || exit "$outcome"
    case "$db_version" in
      037)
        sql -f "$stage/rollback_037_to_034.sql" > "$backup/rollback.log" 2>&1 || exit "$outcome"
        python3 "$stage/fingerprint_guitar_schema.py" kim_prod 034 > "$backup/cutover.rolled-back.json" || exit "$outcome"
        cmp "$backup/cutover.before.json" "$backup/cutover.rolled-back.json" || exit "$outcome"
        ;;
      034) ;;
      *) exit "$outcome" ;;
    esac
    install_binary "$backup/kim-api.034" || exit "$outcome"
    install -o root -g root -m 644 "$backup/guitar-index.034.html" "$index.rollback-037" || exit "$outcome"
    mv -fT "$index.rollback-037" "$index" || exit "$outcome"
    systemctl start "$api" || exit "$outcome"
    ready || exit "$outcome"
    systemctl start "$gateway" || exit "$outcome"
    printf 'Recovered the 034 service before reopening writes. Backup: %s\n' "$backup" >&2
  fi
  exit "$outcome"
}
trap recover EXIT
frozen=1
systemctl stop "$gateway"
systemctl stop "$api"
active=$(sql -c "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
 AND pid<>pg_backend_pid() AND (usename='kim_note_prod_runtime_20261116' OR xact_start IS NOT NULL)")
test "$active" = 0
runuser -u postgres -- pg_dump -Fc -Z 1 --lock-wait-timeout=5s kim_prod > "$backup/kim_prod_034.dump.partial"
mv "$backup/kim_prod_034.dump.partial" "$backup/kim_prod_034.dump"
pg_restore --list "$backup/kim_prod_034.dump" > "$backup/restore-inventory.txt"
(cd "$backup" && sha256sum kim_prod_034.dump > backup.sha256 && sha256sum -c backup.sha256)
python3 "$stage/fingerprint_guitar_schema.py" kim_prod 034 > "$backup/cutover.before.json"

runuser -u postgres -- env DATABASE_URL="$dsn" PGOPTIONS='-c lock_timeout=5000 -c statement_timeout=60000' "$candidate" migrate
sql -f "$stage/mark_guitar_legacy_difficulty.sql" < "$stage/legacy-difficulty.csv" > "$backup/legacy-difficulty.log"
runuser -u postgres -- env DATABASE_URL="$dsn" "$candidate" migration-status
test "$(version)" = 037
test "$(sql -c "SELECT count(*) FROM pg_tables WHERE schemaname='kimcloud'")" = 36
test "$(sql -c "SELECT count(*) FROM pg_tables WHERE schemaname='kimcloud_archive'")" = 6
runtime_role=kim_note_prod_runtime_20261116
sql <<'SQL'
SELECT format('GRANT USAGE ON SEQUENCE %s TO %I',
 pg_get_serial_sequence('kimcloud.guitar_score_files','id'),'kim_note_prod_runtime_20261116')
WHERE NOT has_sequence_privilege('kim_note_prod_runtime_20261116',pg_get_serial_sequence('kimcloud.guitar_score_files','id'),'USAGE')
\gexec
SQL
test "$(sql -c "SELECT (SELECT bool_and(has_table_privilege('$runtime_role',tbl,privilege))
   FROM unnest(ARRAY['kimcloud.guitar_members','kimcloud.guitar_score_files']) AS t(tbl)
   CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE']) AS p(privilege))
 AND has_sequence_privilege('$runtime_role',pg_get_serial_sequence('kimcloud.guitar_score_files','id'),'USAGE')")" = t
python3 "$stage/fingerprint_guitar_schema.py" kim_prod 037 > "$backup/cutover.after.json"
cmp "$backup/cutover.before.json" "$backup/cutover.after.json"

install_binary "$candidate"
systemctl start "$api"
ready
python3 "$stage/install-practice-data-release.py" apply "$stage"
# Past this point clients can write. Reverting uses a fresh snapshot and the
# rehearsed inverse migration, never the old pre-cutover snapshot.
touch "$backup/traffic-reopened"
systemctl start "$gateway"
systemctl is-active "$api" "$gateway" nginx
frozen=0
sha256sum "$binary" "$index"
printf 'Production is on schema 037, 36 active tables and 6 archive tables.\n'
