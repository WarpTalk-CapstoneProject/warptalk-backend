#!/usr/bin/env bash
# ====================================================================
# WarpTalk Backend — Run All Services Locally (dotnet run)
# Starts all microservices in background processes.
# Usage: ./run-all-local.sh
# Stop:  ./run-all-local.sh stop
# ====================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PIDS_FILE="$SCRIPT_DIR/.local-pids"

# ── Colors ────────────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# ── Stop all services ─────────────────────────────────────────────────
stop_all() {
    echo -e "${YELLOW}Stopping all backend services...${NC}"
    if [ -f "$PIDS_FILE" ]; then
        while read -r pid name; do
            if kill -0 "$pid" 2>/dev/null; then
                kill "$pid" 2>/dev/null && echo -e "  ${RED}✗${NC} Stopped $name (PID $pid)"
            fi
        done < "$PIDS_FILE"
        rm -f "$PIDS_FILE"
    else
        echo "  No PID file found. Killing any dotnet processes..."
        pkill -f "dotnet run" 2>/dev/null || true
    fi
    echo -e "${GREEN}All services stopped.${NC}"
    exit 0
}

if [ "${1:-}" = "stop" ]; then
    stop_all
fi

# ── Service definitions ──────────────────────────────────────────────
declare -a SERVICES=(
    "gateway/src/WarpTalk.Gateway|Gateway|5200"
    "auth/src/WarpTalk.AuthService.API|Auth|5001"
    "translation-room/src/WarpTalk.TranslationRoomService.API|TranslationRoom|5242"
    "transcript/src/WarpTalk.TranscriptService.API|Transcript|5214"
    "notification/src/WarpTalk.NotificationService.API|Notification|5209"
    "billing/src/WarpTalk.BillingService.API|Billing|5201"
    "meeting/src/WarpTalk.MeetingService.API|Meeting|5105"
)

# ── Clean old PIDs ────────────────────────────────────────────────────
rm -f "$PIDS_FILE"

echo -e "${GREEN}╔══════════════════════════════════════════╗${NC}"
echo -e "${GREEN}║   WarpTalk Backend — Local Startup       ║${NC}"
echo -e "${GREEN}╚══════════════════════════════════════════╝${NC}"
echo ""

# ── Start each service ────────────────────────────────────────────────
for entry in "${SERVICES[@]}"; do
    IFS='|' read -r project name port <<< "$entry"
    project_path="$SCRIPT_DIR/$project"

    if [ ! -d "$project_path" ]; then
        echo -e "  ${YELLOW}⚠${NC}  Skipping $name — directory not found: $project"
        continue
    fi

    echo -e "  ${GREEN}▶${NC}  Starting ${name} on port ${port}..."
    dotnet run --project "$project_path" --launch-profile "http" --no-build > /dev/null 2>&1 &
    pid=$!
    echo "$pid $name" >> "$PIDS_FILE"
done

echo ""
echo -e "${GREEN}All services started. PIDs saved to .local-pids${NC}"
echo ""
echo -e "  Gateway:         http://localhost:5200"
echo -e "  Auth:            http://localhost:5001"
echo -e "  TranslationRoom: http://localhost:5242"
echo -e "  Transcript:      http://localhost:5214"
echo -e "  Notification:    http://localhost:5209"
echo -e "  Billing:         http://localhost:5201"
echo -e "  Meeting:         http://localhost:5105"
echo ""
echo -e "${YELLOW}To stop all: ./run-all-local.sh stop${NC}"
