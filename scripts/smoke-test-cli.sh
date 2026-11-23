#!/bin/sh
# Checks a published thixsync-cli (trimmed on Windows) before it is shipped: version, SQLite, the JSON of the export (enums,
# dictionaries, times), the services and the exit codes – in a data folder of its own, never the real data.
#
#   scripts/smoke-test-cli.sh <path to thixsync-cli or thixsync-cli.exe>
#
# Runs on macOS and in Git Bash on Windows (CI). No network, no engine: nothing is synced.
set -eu

cli="$1"
data="$(mktemp -d)"
trap 'rm -rf "$data"' EXIT
THIXSYNC_DATA_DIR="$data"
# a Windows program wants a Windows path
if command -v cygpath >/dev/null 2>&1; then THIXSYNC_DATA_DIR="$(cygpath -w "$data")"; fi
export THIXSYNC_DATA_DIR

"$cli" --version

cat > "$data/import.json" <<'JSON'
{
  "format": "thixsync-configuration",
  "version": 1,
  "connections": [
    { "id": "6f0f6d2e-0000-4000-8000-000000000001", "name": "Smoke Quelle", "kind": "WebDav",
      "settings": { "url": "https://dav.example.com/quelle/", "user": "smoke" } },
    { "id": "6f0f6d2e-0000-4000-8000-000000000002", "name": "Smoke Ziel", "kind": "WebDav",
      "settings": { "url": "https://dav.example.com/ziel/" } }
  ],
  "jobs": [
    { "id": "6f0f6d2e-0000-4000-8000-000000000003", "name": "Smoke-Auftrag", "mode": "Mirror",
      "sourceConnectionId": "6f0f6d2e-0000-4000-8000-000000000001",
      "targetConnectionId": "6f0f6d2e-0000-4000-8000-000000000002",
      "pairs": [ { "sourcePath": "/fotos", "targetPath": "/backup/fotos" } ],
      "schedule": { "kind": "Weekly", "timeOfDay": "06:30:00", "days": [ "Monday", "Friday" ] },
      "options": { "conflicts": "NewerWins" } }
  ]
}
JSON

"$cli" import "$data/import.json"
"$cli" list
"$cli" list --json | grep -q '"schedule": "Mo, Fr, 06:30"'
"$cli" connections --json | grep -q '"name": "Smoke Quelle"'
"$cli" pause Smoke-Auftrag
"$cli" list --json | grep -q '"paused": true'
"$cli" runs --json | grep -q '^\[\]'
"$cli" export "$data/export.json"
grep -q '"conflicts": "NewerWins"' "$data/export.json"

# importing the same file again changes nothing; a wrong call and an unknown job have their exit codes
"$cli" import "$data/import.json" | grep -q '0 importiert, 3 übersprungen, 0 abgelehnt.'
code=0; "$cli" frob 2>/dev/null || code=$?
[ "$code" = 3 ] || { echo "exit code for a wrong call: $code (expected 3)"; exit 1; }
code=0; "$cli" run "Gibt es nicht" 2>/dev/null || code=$?
[ "$code" = 4 ] || { echo "exit code for an unknown job: $code (expected 4)"; exit 1; }

echo "thixsync-cli smoke test passed"
