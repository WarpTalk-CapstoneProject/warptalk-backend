#!/bin/sh
# Seed the assistant plugin catalog with the remote MCP apps in catalog-seed.json and
# catalog-seed-google.json.
#
# Each entry is one POST /api/v1/assistant/plugins/catalog: the WT-602 ladder resolves the OAuth
# client on the first connect, so most entries need no client id up front.
#
# Ladder outcome per server, as probed live on 2026-09-04:
#   rung 3 DCR (connects with no operator work):  linear notion atlassian asana canva zapier monday
#   rung 2 CIMD also advertised (used once Plugins:Mcp:Client:ClientMetadataUrl is public): linear notion canva
#   rung 1 only (GitHub, Slack, HubSpot): their authorization servers support neither CIMD nor DCR.
#     They are seeded anyway so the catalog is complete; Connect shows the "needs operator setup"
#     card until an operator registers an OAuth app with the provider and re-creates the row with
#     an "oauth": { "clientId": ..., "clientSecret": ... } block (rung 1).
#   figma: advertises DCR but its registration endpoint answers 403 to every registration
#     (redirect URI scheme makes no difference); treat like rung 1 until Figma opens DCR.
#   Google Workspace (catalog-seed-google.json, 8 servers under *.googleapis.com/mcp/v1): rung 1.
#     Google's authorization server offers neither DCR nor CIMD, so the entries carry the project's
#     Google OAuth client through "${GOOGLE_WORKSPACE_CLIENT_ID}" / "${GOOGLE_WORKSPACE_CLIENT_SECRET}"
#     placeholders, expanded from the environment here and never written to disk. Entries whose
#     placeholders expand to nothing are skipped with a message. Before Connect can succeed the
#     operator must also: enrol the Cloud project in the Workspace Developer Preview Program,
#     enable each *MCP service, add every scope below to the consent screen, and add
#     <gateway>/api/v1/assistant/plugins/mcp/oauth/callback as an authorised redirect URI.
#
# Idempotent: an entry whose key already exists is reported and skipped (the API answers 400).
#
#   set -a; . ../../../../../warptalk-infrastructure/.env; set +a   # for the Google placeholders
#   GATEWAY_URL=http://localhost:5200 ADMIN_TOKEN=<jwt with platform 'admin' role> ./seed-catalog.sh
set -eu

GATEWAY_URL="${GATEWAY_URL:-http://localhost:5200}"
: "${ADMIN_TOKEN:?Set ADMIN_TOKEN to a JWT carrying the platform 'admin' role}"
HERE="$(dirname "$0")"
SEED_FILES="${SEED_FILES:-$HERE/catalog-seed.json $HERE/catalog-seed-google.json}"

command -v python3 >/dev/null || { echo "python3 is required to read the seed files" >&2; exit 1; }

# One JSON object per line, placeholders expanded from the environment. An entry with an "oauth"
# block whose clientId is empty after expansion is emitted as a SKIP line instead.
python3 - $SEED_FILES <<'EOF' | while IFS= read -r line; do
import json, os, re, sys
def expand(value):
    if isinstance(value, str):
        return re.sub(r"\$\{([A-Z0-9_]+)\}", lambda m: os.environ.get(m.group(1), ""), value)
    if isinstance(value, dict):
        return {k: expand(v) for k, v in value.items()}
    if isinstance(value, list):
        return [expand(v) for v in value]
    return value
for path in sys.argv[1:]:
    for entry in json.load(open(path, encoding="utf-8")):
        entry = expand(entry)
        oauth = entry.get("oauth")
        if oauth is not None and not oauth.get("clientId"):
            print("SKIP " + entry["pluginKey"])
            continue
        print(json.dumps(entry, ensure_ascii=False))
EOF
  case "$line" in
    SKIP\ *)
      echo "  skipped  ${line#SKIP } (no OAuth client id in the environment; rung 1 needs one)"
      continue ;;
  esac
  key="$(printf '%s' "$line" | python3 -c 'import json,sys; print(json.load(sys.stdin)["pluginKey"])')"
  status="$(printf '%s' "$line" | curl -s -o /tmp/seed-catalog.out -w '%{http_code}' \
    -X POST "$GATEWAY_URL/api/v1/assistant/plugins/catalog" \
    -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" --data-binary @-)"
  case "$status" in
    201) echo "  created  $key" ;;
    400) echo "  skipped  $key ($(cat /tmp/seed-catalog.out))" ;;
    *)   echo "  FAILED   $key http=$status $(cat /tmp/seed-catalog.out)" >&2; exit 1 ;;
  esac
done
