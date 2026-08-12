#!/usr/bin/env bash
set -euo pipefail

# The one runtime base every production image is allowed to sit on, pinned by digest so a
# floating tag cannot move it underneath us.
#
# Bumping this line is the WHOLE point of the check: the nine Dockerfiles and this value move
# together, in one commit, or CI fails. That is what stops one service quietly running an
# unpatched runtime while the others are fixed.
#
# 10.0.10 -> 10.0.11 on 2026-08-12 for CVE-2026-62901 (.NET denial of service, HIGH), which the
# release's own Trivy gate caught. Verified before pinning: Trivy reports 0 HIGH / 0 CRITICAL
# against this digest.
expected='FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS final'
status=0

while IFS= read -r dockerfile; do
  if ! grep -Fqx "$expected" "$dockerfile"; then
    printf 'Production runtime base is not pinned to the approved patched image: %s\n' "$dockerfile" >&2
    status=1
  fi
done <<'EOF'
auth/Dockerfile
billing/Dockerfile
assistant/Dockerfile
gateway/src/WarpTalk.Gateway/Dockerfile
meeting/Dockerfile
notification/Dockerfile
transcript/Dockerfile
translation-room/Dockerfile
workspace/Dockerfile
EOF

exit "$status"
