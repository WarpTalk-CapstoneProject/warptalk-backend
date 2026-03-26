#!/usr/bin/env bash
# ====================================================================
# WarpTalk Backend — Start All Microservices
# Runs PostgreSQL in Docker, all .NET services natively via dotnet run
#
# Usage:
#   ./start-all.sh          # Start all services (foreground logs)
#   ./start-all.sh --detach # Start all services in background
#   ./start-all.sh --stop   # Stop all services
#   ./start-all.sh --status # Check status of all services
# ====================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOG_DIR="/tmp/warptalk_logs"
PG_CONTAINER="warptalk-postgres"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

# Service definitions: name|cwd|port
SERVICES=(
    "auth|auth/src/WarpTalk.AuthService.API|5101"
    "meeting|meeting/src/WarpTalk.MeetingService.API|5102"
    "transcript|transcript/src/WarpTalk.TranscriptService.API|5103"
    "notification|notification/src/WarpTalk.NotificationService.API|5104"
    "gateway|gateway/src/WarpTalk.Gateway|5200"
)

print_banner() {
    echo -e "${CYAN}"
    echo "╔══════════════════════════════════════════════════════╗"
    echo "║             🚀 WarpTalk Backend Stack               ║"
    echo "╠══════════════════════════════════════════════════════╣"
    echo "║  PostgreSQL  (Docker)         → localhost:5432       ║"
    echo "║  Auth        (REST+gRPC)      → :5101 / :50051      ║"
    echo "║  Meeting     (REST+gRPC)      → :5102 / :50052      ║"
    echo "║  Transcript  (REST+gRPC)      → :5103 / :50053      ║"
    echo "║  Notification (REST)          → :5104 / :50054      ║"
    echo "║  Gateway     (YARP+SignalR)   → :5200                ║"
    echo "║                                                      ║"
    echo "║  SignalR Hubs:                                       ║"
    echo "║    Meeting      → ws://localhost:5200/hubs/meeting   ║"
    echo "║    Notification → ws://localhost:5200/hubs/notification║"
    echo "╚══════════════════════════════════════════════════════╝"
    echo -e "${NC}"
}

kill_ports() {
    echo -e "${YELLOW}🧹 Cleaning up occupied ports...${NC}"
    for port in 5101 5102 5103 5104 5200 50051 50052 50053 50054; do
        local pids
        pids=$(lsof -ti :"$port" 2>/dev/null || true)
        if [[ -n "$pids" ]]; then
            echo "$pids" | xargs kill -9 2>/dev/null || true
            echo -e "   Killed process(es) on port $port"
        fi
    done
}

start_postgres() {
    echo -e "${CYAN}🐘 Starting PostgreSQL...${NC}"
    if docker ps --format '{{.Names}}' | grep -q "^${PG_CONTAINER}$"; then
        echo -e "   ${GREEN}Already running${NC}"
    elif docker ps -a --format '{{.Names}}' | grep -q "^${PG_CONTAINER}$"; then
        docker start "$PG_CONTAINER" > /dev/null
        echo -e "   ${GREEN}Started existing container${NC}"
    else
        docker run -d \
            --name "$PG_CONTAINER" \
            -e POSTGRES_DB=warptalk \
            -e POSTGRES_USER=postgres \
            -e POSTGRES_PASSWORD=postgres \
            -p 5432:5432 \
            postgres:16-alpine > /dev/null
        echo -e "   ${GREEN}Created and started new container${NC}"
    fi

    # Wait until PostgreSQL is ready
    echo -ne "   Waiting for PostgreSQL to be ready"
    for i in $(seq 1 30); do
        if docker exec "$PG_CONTAINER" pg_isready -U postgres -q 2>/dev/null; then
            echo -e " ${GREEN}✅${NC}"
            return
        fi
        echo -n "."
        sleep 1
    done
    echo -e " ${RED}❌ Timeout${NC}"
    exit 1
}

start_services_bg() {
    mkdir -p "$LOG_DIR"

    # Start microservices first, gateway last
    for entry in "${SERVICES[@]}"; do
        IFS='|' read -r name cwd port <<< "$entry"
        echo -e "${CYAN}🚀 Starting ${name}...${NC}"

        cd "$SCRIPT_DIR/$cwd"

        if [[ "$name" == "gateway" ]]; then
            # Gateway needs explicit URL since it has no Kestrel override in Program.cs
            ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:5200" \
                dotnet run --no-launch-profile > "$LOG_DIR/${name}.log" 2>&1 &
        else
            ASPNETCORE_ENVIRONMENT=Development \
                dotnet run --no-launch-profile > "$LOG_DIR/${name}.log" 2>&1 &
        fi
        local pid=$!
        echo "$pid" > "$LOG_DIR/${name}.pid"
        echo -e "   ${GREEN}PID: $pid → log: $LOG_DIR/${name}.log${NC}"

        # Small delay between services for startup ordering
        if [[ "$name" == "auth" ]]; then
            sleep 3  # Auth must be ready before others connect via gRPC
        else
            sleep 1
        fi
    done
}

wait_and_test() {
    echo ""
    echo -e "${YELLOW}⏳ Waiting 8s for all services to initialize...${NC}"
    sleep 8

    echo -e "${CYAN}🔍 Service health check:${NC}"
    local all_ok=true

    for entry in "${SERVICES[@]}"; do
        IFS='|' read -r name cwd port <<< "$entry"

        if [[ -f "$LOG_DIR/${name}.pid" ]]; then
            local pid
            pid=$(cat "$LOG_DIR/${name}.pid")
            if kill -0 "$pid" 2>/dev/null; then
                echo -e "   ${GREEN}✅ ${name} (PID: $pid, port: $port)${NC}"
            else
                echo -e "   ${RED}❌ ${name} — process died! Check: tail $LOG_DIR/${name}.log${NC}"
                all_ok=false
            fi
        fi
    done

    echo ""

    # Test Gateway health
    echo -e "${CYAN}🧪 Testing Gateway...${NC}"
    local health_code
    health_code=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5200/health 2>/dev/null || echo "000")
    if [[ "$health_code" == "200" ]]; then
        echo -e "   ${GREEN}✅ Gateway /health → 200 OK${NC}"
    else
        echo -e "   ${YELLOW}⚠  Gateway /health → HTTP $health_code${NC}"
    fi

    # Test Auth registration endpoint via Gateway
    echo -e "${CYAN}🧪 Testing Auth route via Gateway...${NC}"
    local auth_code
    auth_code=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5200/api/v1/auth/register 2>/dev/null || echo "000")
    echo -e "   POST /api/v1/auth/register → HTTP $auth_code"

    # Test SignalR negotiate (should return 401 without JWT)
    echo -e "${CYAN}🧪 Testing SignalR negotiate...${NC}"
    local signalr_code
    signalr_code=$(curl -s -o /dev/null -w "%{http_code}" \
        -X POST http://localhost:5200/hubs/meeting/negotiate?negotiateVersion=1 2>/dev/null || echo "000")
    if [[ "$signalr_code" == "401" ]]; then
        echo -e "   ${GREEN}✅ /hubs/meeting/negotiate → 401 (JWT required, correct!)${NC}"
    elif [[ "$signalr_code" == "200" ]]; then
        echo -e "   ${RED}⚠  /hubs/meeting/negotiate → 200 (auth not enforced!)${NC}"
    else
        echo -e "   ${YELLOW}⚠  /hubs/meeting/negotiate → HTTP $signalr_code${NC}"
    fi

    signalr_code=$(curl -s -o /dev/null -w "%{http_code}" \
        -X POST http://localhost:5200/hubs/notification/negotiate?negotiateVersion=1 2>/dev/null || echo "000")
    if [[ "$signalr_code" == "401" ]]; then
        echo -e "   ${GREEN}✅ /hubs/notification/negotiate → 401 (JWT required, correct!)${NC}"
    else
        echo -e "   ${YELLOW}⚠  /hubs/notification/negotiate → HTTP $signalr_code${NC}"
    fi

    echo ""
    echo -e "${CYAN}📋 Useful commands:${NC}"
    echo -e "   View logs:  ${YELLOW}tail -f $LOG_DIR/*.log${NC}"
    echo -e "   Stop all:   ${YELLOW}./start-all.sh --stop${NC}"
    echo -e "   Status:     ${YELLOW}./start-all.sh --status${NC}"
}

show_status() {
    echo -e "${CYAN}📋 WarpTalk Service Status:${NC}"

    # PostgreSQL
    if docker ps --format '{{.Names}}' | grep -q "^${PG_CONTAINER}$"; then
        echo -e "   ${GREEN}✅ PostgreSQL (Docker: $PG_CONTAINER)${NC}"
    else
        echo -e "   ${RED}❌ PostgreSQL (Docker: $PG_CONTAINER)${NC}"
    fi

    # .NET services
    for entry in "${SERVICES[@]}"; do
        IFS='|' read -r name cwd port <<< "$entry"
        if [[ -f "$LOG_DIR/${name}.pid" ]]; then
            local pid
            pid=$(cat "$LOG_DIR/${name}.pid")
            if kill -0 "$pid" 2>/dev/null; then
                echo -e "   ${GREEN}✅ ${name} (PID: $pid, port: $port)${NC}"
            else
                echo -e "   ${RED}❌ ${name} (dead)${NC}"
            fi
        else
            echo -e "   ${YELLOW}⚪ ${name} (no PID file)${NC}"
        fi
    done
}

stop_services() {
    echo -e "${YELLOW}⏹  Stopping all services...${NC}"

    for entry in "${SERVICES[@]}"; do
        IFS='|' read -r name cwd port <<< "$entry"
        if [[ -f "$LOG_DIR/${name}.pid" ]]; then
            local pid
            pid=$(cat "$LOG_DIR/${name}.pid")
            if kill -0 "$pid" 2>/dev/null; then
                kill "$pid" 2>/dev/null || true
                echo -e "   Stopped $name (PID: $pid)"
            fi
            rm -f "$LOG_DIR/${name}.pid"
        fi
    done

    kill_ports
    echo -e "${GREEN}✅ All .NET services stopped.${NC}"
    echo -e "${YELLOW}   Note: PostgreSQL container left running. Stop with: docker stop $PG_CONTAINER${NC}"
}

# ─── Main ─────────────────────────────────────────────────────────────
case "${1:-}" in
    --stop)
        stop_services
        ;;
    --status)
        show_status
        ;;
    *)
        print_banner
        kill_ports
        start_postgres
        start_services_bg
        wait_and_test
        ;;
esac
