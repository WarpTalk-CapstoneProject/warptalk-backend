#!/usr/bin/env bash
# ====================================================================
# WarpTalk Backend — Run All .NET Services Locally (no Docker)
# Just runs all dotnet services concurrently. Ctrl+C to stop all.
# Usage: ./run-all-local.sh
# ====================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
NC='\033[0m'

PIDS=()

cleanup() {
    echo ""
    echo -e "${YELLOW}Stopping all services...${NC}"
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null
    echo -e "${GREEN}All services stopped.${NC}"
    exit 0
}

trap cleanup SIGINT SIGTERM

echo -e "${CYAN}╔══════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║   WarpTalk Backend — All Services Local  ║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════╝${NC}"
echo ""

declare -a SERVICES=(
    "auth/src/WarpTalk.AuthService.API|Auth|5001"
    "translation-room/src/WarpTalk.TranslationRoomService.API|TranslationRoom|5242"
    "transcript/src/WarpTalk.TranscriptService.API|Transcript|5214"
    "notification/src/WarpTalk.NotificationService.API|Notification|5209"
    "billing/src/WarpTalk.BillingService.API|Billing|5201"
    "meeting/src/WarpTalk.MeetingService.API|Meeting|5105"
    "gateway/src/WarpTalk.Gateway|Gateway|5200"
)

for entry in "${SERVICES[@]}"; do
    IFS='|' read -r project name port <<< "$entry"
    
    if [ ! -d "$SCRIPT_DIR/$project" ]; then
        echo -e "  ${YELLOW}⚠ Skip $name — not found${NC}"
        continue
    fi

    echo -e "  ${GREEN}▶${NC} $name → http://localhost:$port"
    dotnet run --project "$SCRIPT_DIR/$project" --launch-profile "http" &
    PIDS+=($!)
done

echo ""
echo -e "${GREEN}All services started. Press Ctrl+C to stop all.${NC}"
echo ""

wait
