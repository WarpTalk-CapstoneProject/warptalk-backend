#!/usr/bin/env bash
set -euo pipefail

expected='FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final'
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
