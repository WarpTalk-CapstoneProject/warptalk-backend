#!/usr/bin/env bash
# ====================================================================
# WarpTalk Backend — Run All Services via Docker Compose
# Usage: ./run-all-docker.sh          (start)
#        ./run-all-docker.sh stop     (stop)
#        ./run-all-docker.sh rebuild  (rebuild & start)
#        ./run-all-docker.sh logs     (follow logs)
# ====================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DOCKER="/Applications/Docker.app/Contents/Resources/bin/docker"

# Fallback to PATH docker if the macOS app path doesn't exist
if [ ! -x "$DOCKER" ]; then
    DOCKER="docker"
fi

COMPOSE="$DOCKER compose"

# ── Colors ────────────────────────────────────────────────────────────
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

cd "$SCRIPT_DIR"

case "${1:-start}" in
    stop)
        echo -e "${RED}Stopping all Docker services...${NC}"
        $COMPOSE down
        echo -e "${GREEN}All containers stopped.${NC}"
        ;;
    rebuild)
        echo -e "${YELLOW}Rebuilding and starting all Docker services...${NC}"
        $COMPOSE up --build -d
        echo ""
        echo -e "${GREEN}All services rebuilt and started.${NC}"
        $COMPOSE ps
        ;;
    logs)
        $COMPOSE logs -f --tail=50
        ;;
    start|"")
        echo -e "${GREEN}╔══════════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║   WarpTalk Backend — Docker Startup      ║${NC}"
        echo -e "${GREEN}╚══════════════════════════════════════════╝${NC}"
        echo ""
        $COMPOSE up -d
        echo ""
        echo -e "${GREEN}All containers running:${NC}"
        $COMPOSE ps
        echo ""
        echo -e "${YELLOW}Useful commands:${NC}"
        echo -e "  ./run-all-docker.sh logs     — follow all logs"
        echo -e "  ./run-all-docker.sh stop     — stop all containers"
        echo -e "  ./run-all-docker.sh rebuild  — rebuild & restart"
        ;;
    *)
        echo "Usage: $0 {start|stop|rebuild|logs}"
        exit 1
        ;;
esac
