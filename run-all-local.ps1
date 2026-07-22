# ====================================================================
# WarpTalk Backend - Run All .NET Services Locally (no Docker)
# Manages PostgreSQL + Redis + RabbitMQ + LiveKit in Docker,
# and all .NET services natively via PowerShell.
#
# Usage:
#   .\run-all-local.ps1           # Start all services
#   .\run-all-local.ps1 -Stop     # Stop all running services
#   .\run-all-local.ps1 -Status   # Check running status of services
# ====================================================================

[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Status
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$LogDir = Join-Path $ScriptDir "logs"
$PGContainer = "warptalk-postgres"
$RedisContainer = "warptalk-redis"
$RabbitContainer = "warptalk-rabbitmq"
$LiveKitContainer = "warptalk-livekit"
$PidFile = Join-Path $LogDir "pids.json"

# ANSI Colors
$ESC    = [char]27
$RED    = "$ESC[0;31m"
$GREEN  = "$ESC[0;32m"
$YELLOW = "$ESC[1;33m"
$CYAN   = "$ESC[0;36m"
$NC     = "$ESC[0m"

# Service definitions: Name | Folder Cwd | Port
$Services = @(
    [PSCustomObject]@{ Name = "auth";             Cwd = "auth/src/WarpTalk.AuthService.API";             Port = 5101 },
    [PSCustomObject]@{ Name = "workspace";        Cwd = "workspace/src/WarpTalk.WorkspaceService.API";        Port = 5106 },
    [PSCustomObject]@{ Name = "translation-room";  Cwd = "translation-room/src/WarpTalk.TranslationRoomService.API"; Port = 5102 },
    [PSCustomObject]@{ Name = "transcript";       Cwd = "transcript/src/WarpTalk.TranscriptService.API";       Port = 5103 },
    [PSCustomObject]@{ Name = "notification";     Cwd = "notification/src/WarpTalk.NotificationService.API";     Port = 5104 },
    [PSCustomObject]@{ Name = "meeting";          Cwd = "meeting/src/WarpTalk.MeetingService.API";          Port = 5105 },
    [PSCustomObject]@{ Name = "billing";          Cwd = "billing/src/WarpTalk.BillingService.API";          Port = 5107 },
    [PSCustomObject]@{ Name = "payment";          Cwd = "payment/src/WarpTalk.PaymentService.API";          Port = 5047 },
    [PSCustomObject]@{ Name = "assistant";        Cwd = "assistant/src/WarpTalk.AssistantService.API";      Port = 5108 },
    [PSCustomObject]@{ Name = "gateway";          Cwd = "gateway/src/WarpTalk.Gateway";                  Port = 5200 }
)

# Load .env variables
$envFile = Join-Path $ScriptDir "..\warptalk-infrastructure\.env"
if (Test-Path $envFile) {
    Write-Host ($CYAN + "[ENV] Loading environment variables from .env..." + $NC)
    Get-Content $envFile | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line -match '=') {
            $key, $value = $line -split '=', 2
            $env:$key = $value.Trim()
        }
    }
}

# Set default credentials if not found in .env
if (-not $env:POSTGRES_USER) { $env:POSTGRES_USER = "postgres" }
if (-not $env:POSTGRES_DB) { $env:POSTGRES_DB = "warptalk" }
if (-not $env:POSTGRES_PASSWORD) { $env:POSTGRES_PASSWORD = "postgres" }
if (-not $env:REDIS_PASSWORD) { $env:REDIS_PASSWORD = "CHANGE_ME_REDIS_PASSWORD" }

function Show-Banner {
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ($CYAN + "|             *** WarpTalk Backend Stack ***           |" + $NC)
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ($CYAN + "|  PostgreSQL  (Docker)         -> localhost:5432      |" + $NC)
    Write-Host ($CYAN + "|  Redis       (Docker)         -> localhost:6379      |" + $NC)
    Write-Host ($CYAN + "|  RabbitMQ    (Docker)         -> localhost:5672      |" + $NC)
    Write-Host ($CYAN + "|  LiveKit     (Docker)         -> localhost:7880      |" + $NC)
    Write-Host ($CYAN + "|  Auth        (REST+gRPC)      -> :5101 / :50051      |" + $NC)
    Write-Host ($CYAN + "|  Workspace   (REST+gRPC)      -> :5106 / :50056      |" + $NC)
    Write-Host ($CYAN + "|  Translation (REST+gRPC)      -> :5102 / :50052      |" + $NC)
    Write-Host ($CYAN + "|  Transcript  (REST+gRPC)      -> :5103 / :50053      |" + $NC)
    Write-Host ($CYAN + "|  Notification(REST+gRPC)      -> :5104 / :50054      |" + $NC)
    Write-Host ($CYAN + "|  Meeting     (REST+gRPC)      -> :5105 / :50055      |" + $NC)
    Write-Host ($CYAN + "|  Assistant   (REST+SignalR)   -> :5108               |" + $NC)
    Write-Host ($CYAN + "|  Gateway     (YARP+SignalR)   -> :5200               |" + $NC)
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ""
}

function Stop-Ports {
    Write-Host ($YELLOW + "[CLEAN] Cleaning up occupied ports..." + $NC)
    $ports = @(5101, 5102, 5103, 5104, 5105, 5106, 5107, 5108, 5047, 5200, 50051, 50052, 50053, 50054, 50055, 50056, 50057, 50058)
    foreach ($port in $ports) {
        $nets = netstat -ano | Select-String ":$port\s+"
        foreach ($line in $nets) {
            if ($line -match '\s+(\d+)$') {
                $pidVal = $Matches[1]
                if ($pidVal -and $pidVal -ne 0) {
                    try {
                        Stop-Process -Id $pidVal -Force -ErrorAction SilentlyContinue
                        Write-Host "   Killed process $pidVal on port $port"
                    } catch {}
                }
            }
        }
    }

    Write-Host ($YELLOW + "[CLEAN] Cleaning up lingering dotnet processes..." + $NC)
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    try { dotnet build-server shutdown 2>$null } catch {}
}

# Clean conflicting exited containers with matching names
function Remove-ConflictingContainer ($containerName) {
    $exists = docker ps -a --filter "name=^/$containerName$" --format "{{.ID}}"
    if ($exists) {
        $running = docker ps --filter "name=^/$containerName$" --format "{{.ID}}"
        if (-not $running) {
            Write-Host ($YELLOW + "   Removing inactive container: $containerName" + $NC)
            docker rm $containerName | Out-Null
        }
    }
}

function Start-Postgres {
    Write-Host ($CYAN + "[DB] Starting PostgreSQL..." + $NC)
    Remove-ConflictingContainer $PGContainer
    $running = docker ps --filter "name=^/$PGContainer$" --format "{{.Names}}"
    $exists = docker ps -a --filter "name=^/$PGContainer$" --format "{{.Names}}"

    if ($running) {
        Write-Host ($GREEN + "   Already running" + $NC)
    } elseif ($exists) {
        docker start $PGContainer | Out-Null
        Write-Host ($GREEN + "   Started existing container" + $NC)
    } else {
        docker run -d `
            --name $PGContainer `
            -e POSTGRES_DB=$env:POSTGRES_DB `
            -e POSTGRES_USER=$env:POSTGRES_USER `
            -e POSTGRES_PASSWORD=$env:POSTGRES_PASSWORD `
            -p 5432:5432 `
            postgres:18-alpine | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }

    # Wait until healthy
    Write-Host -NoNewline "   Waiting for PostgreSQL to be ready"
    for ($i = 1; $i -le 30; $i++) {
        $null = docker exec $PGContainer pg_isready -U postgres 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ($GREEN + " [OK]" + $NC)
            
            # Ensure the password matches the .env file in case the container was pre-initialized with a different one
            Write-Host "   Synchronizing database password credentials..."
            docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB -c "ALTER USER postgres WITH PASSWORD '$($env:POSTGRES_PASSWORD)';" | Out-Null
            return
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 1
    }
    Write-Host ($RED + " [TIMEOUT]" + $NC)
    exit 1
}

function Start-Redis {
    Write-Host ($CYAN + "[REDIS] Starting Redis..." + $NC)
    Remove-ConflictingContainer $RedisContainer
    $running = docker ps --filter "name=^/$RedisContainer$" --format "{{.Names}}"
    $exists = docker ps -a --filter "name=^/$RedisContainer$" --format "{{.Names}}"

    if ($running) {
        Write-Host ($GREEN + "   Already running" + $NC)
    } elseif ($exists) {
        docker start $RedisContainer | Out-Null
        Write-Host ($GREEN + "   Started existing container" + $NC)
    } else {
        docker run -d `
            --name $RedisContainer `
            -p 6379:6379 `
            redis:7-alpine redis-server --requirepass $env:REDIS_PASSWORD --appendonly yes | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }

    # Wait until healthy
    Write-Host -NoNewline "   Waiting for Redis to be ready"
    for ($i = 1; $i -le 30; $i++) {
        $ping = docker exec $RedisContainer redis-cli -a $env:REDIS_PASSWORD ping 2>$null
        if ($ping -match "PONG") {
            Write-Host ($GREEN + " [OK]" + $NC)
            return
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 1
    }
    Write-Host ($RED + " [TIMEOUT]" + $NC)
    exit 1
}

function Start-RabbitMQ {
    Write-Host ($CYAN + "[RABBITMQ] Starting RabbitMQ..." + $NC)
    Remove-ConflictingContainer $RabbitContainer
    $running = docker ps --filter "name=^/$RabbitContainer$" --format "{{.Names}}"
    $exists = docker ps -a --filter "name=^/$RabbitContainer$" --format "{{.Names}}"

    if ($running) {
        Write-Host ($GREEN + "   Already running" + $NC)
    } elseif ($exists) {
        docker start $RabbitContainer | Out-Null
        Write-Host ($GREEN + "   Started existing container" + $NC)
    } else {
        docker run -d `
            --name $RabbitContainer `
            -p 5672:5672 -p 15672:15672 `
            rabbitmq:4-management-alpine | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }

    # Wait until healthy, checking for permission cookie error
    Write-Host -NoNewline "   Waiting for RabbitMQ to be ready"
    for ($i = 1; $i -le 40; $i++) {
        # Check if container exited (permission issue)
        $status = docker inspect $RabbitContainer --format "{{.State.Status}}" 2>$null
        if ($status -eq "exited") {
            Write-Host ($RED + " [FAILED]" + $NC)
            Write-Host ($YELLOW + "   Permission issue detected with rabbitmq volume. Re-creating volume..." + $NC)
            docker rm $RabbitContainer | Out-Null
            docker volume rm warptalk-infrastructure_rabbitmq-data 2>$null
            
            # Restart setup
            docker run -d `
                --name $RabbitContainer `
                -p 5672:5672 -p 15672:15672 `
                rabbitmq:4-management-alpine | Out-Null
            Write-Host -NoNewline "   Re-waiting for RabbitMQ to be ready"
            continue
        }

        $ping = docker exec $RabbitContainer rabbitmq-diagnostics -q ping 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ($GREEN + " [OK]" + $NC)
            return
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 2
    }
    Write-Host ($RED + " [TIMEOUT]" + $NC)
    exit 1
}

function Start-LiveKit {
    Write-Host ($CYAN + "[LIVEKIT] Starting LiveKit..." + $NC)
    Remove-ConflictingContainer $LiveKitContainer
    $running = docker ps --filter "name=^/$LiveKitContainer$" --format "{{.Names}}"
    $exists = docker ps -a --filter "name=^/$LiveKitContainer$" --format "{{.Names}}"

    if ($running) {
        Write-Host ($GREEN + "   Already running" + $NC)
    } elseif ($exists) {
        docker start $LiveKitContainer | Out-Null
        Write-Host ($GREEN + "   Started existing container" + $NC)
    } else {
        docker run -d `
            --name $LiveKitContainer `
            -p 7880:7880 -p 7881:7881 -p 7882:7882/udp `
            -e LIVEKIT_KEYS="APIBVnfFo9PzzoQ: wbB6j98H2jfF5nLTZYhaiYXQM8hM6nB3KoVoXfMNTPA" `
            livekit/livekit-server:latest --dev --bind 0.0.0.0 | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }
}

function Invoke-Migrations {
    Write-Host ($CYAN + "[DB] Running PostgreSQL migrations..." + $NC)
    $MigrationsDir = Join-Path $ScriptDir "..\warptalk-infrastructure\scripts\migrations"
    
    if (Test-Path $MigrationsDir) {
        # Ordered migration files list as verified by dates & dependencies
        $files = @(
            "000-init-migrations.sql",
            "001-14-04-2026-rename-meeting.sql",
            "002-16-04-2026-rename-meeting-columns.sql",
            "003-17-04-2026-uppercase-type.sql",
            "004-01-05-2026-add-notification-message-table.sql",
            "005-09-05-2026-add-admin-notifications-table.sql",
            "006-14-05-2026-convert-transcript-status-to-enum.sql",
            "006-15-05-2026-rename-participant-is-translation-audio-enabled.sql",
            "007-16-05-2026-add-meeting-schema.sql",
            "008-20-05-2026-add-translation-room-views.sql",
            "007-03-06-2026-separate-workspace-schema-from-auth.sql",
            "008-03-06-2026-add-workspace-documents-and-glossary.sql",
            "009-04-06-2026-add-meeting-chat.sql",
            "009-05-06-2026-rename-role-key-to-subject-key.sql",
            "010-12-06-2026-add-chat-mentions.sql",
            "010-12-06-2026-add-can-create-meetings-to-workspace-members.sql",
            "011-12-06-2026-convert-enums-to-varchar.sql",
            "012-14-06-2026-add-meeting-invitation.sql",
            "013-14-06-2026-add-meeting-active-host.sql",
            "014-15-06-2026-convert-translation-and-transcript-enums-to-varchar.sql",
            "015-16-06-2026-add-translation-room-invitations.sql",
            "016-03-07-2026-enforce-single-active-subscription.sql",
            "016-14-07-2026-remove-is-sensitive-from-workspace-documents.sql",
            "016-16-07-2026-add-segment-id-to-usage-records.sql",
            "017-15-07-2026-translation-cluster-finalize.sql",
            "018-16-07-2026-fix-users-user-settings-fk-direction.sql",
            "019-16-07-2026-billing-schema-mismatch-and-idempotency.sql",
            "020-17-07-2026-refresh-token-family-reuse-detection.sql",
            "021-20-07-2026-add-translation-room-sessions.sql",
            "022-20-07-2026-add-transcript-segment-id-to-billing.sql",
            "023-20-07-2026-switch-payment-provider-to-stripe.sql",
            "024-20-07-2026-drop-transcript-translations.sql"
        )

        # Initialize migrations log table
        Get-Content (Join-Path $MigrationsDir "000-init-migrations.sql") -Raw | docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB | Out-Null

        foreach ($f in $files) {
            if ($f -eq "000-init-migrations.sql") { continue }
            $filePath = Join-Path $MigrationsDir $f
            if (Test-Path $filePath) {
                # Check if already applied
                $isApplied = docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB -tAc "SELECT 1 FROM public.schema_migrations WHERE version='$f';" 2>$null
                if ($isApplied -eq $null -or $isApplied.Trim() -ne "1") {
                    Write-Host "   Executing $f..."
                    Get-Content $filePath -Raw | docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB | Out-Null
                    docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB -c "INSERT INTO public.schema_migrations(version) VALUES ('$f');" | Out-Null
                } else {
                    # Skip
                }
            }
        }
        Write-Host ($GREEN + "   [OK] Migrations completed" + $NC)
    } else {
        Write-Host ($YELLOW + "   [WARN] No migrations directory found at $MigrationsDir" + $NC)
    }
}

function Invoke-Seeds {
    Write-Host ($CYAN + "[DB] Seeding PostgreSQL database..." + $NC)
    $SeedDemo = Join-Path $ScriptDir "..\warptalk-infrastructure\scripts\seed-demo.sql"
    
    if (Test-Path $SeedDemo) {
        $userCount = docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB -tAc "SELECT COUNT(*) FROM auth.users;" 2>$null
        if ($userCount -eq $null -or $userCount.Trim() -eq "0") {
            Write-Host "   Applying seed-demo.sql..."
            Get-Content $SeedDemo -Raw | docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB | Out-Null

            Write-Host "   Applying platform supported languages..."
            $seedLangSql = "INSERT INTO platform.supported_languages (code, name, native_name, stt_supported, translation_supported, tts_supported, voice_clone_supported, is_active, created_at) VALUES ('vi', 'Vietnamese', 'Tiếng Việt', true, true, true, true, true, NOW()), ('en', 'English', 'English', true, true, true, true, true, NOW()), ('ja', 'Japanese', '日本語', true, true, true, false, true, NOW()), ('ko', 'Korean', '한국어', true, true, true, false, true, NOW()), ('zh', 'Chinese', '中文', true, true, true, false, true, NOW()), ('fr', 'French', 'Français', true, true, true, false, true, NOW()), ('es', 'Spanish', 'Español', true, true, true, false, true, NOW()) ON CONFLICT (code) DO UPDATE SET is_active = EXCLUDED.is_active;"
            docker exec -i $PGContainer psql -U postgres -d $env:POSTGRES_DB -c $seedLangSql | Out-Null
            
            Write-Host ($GREEN + "   [OK] Seeding completed" + $NC)
        } else {
            Write-Host "   Database already has users ($($userCount.Trim()) rows). Skipping seeds."
        }
    } else {
        Write-Host ($YELLOW + "   [WARN] Seed data file seed-demo.sql not found" + $NC)
    }
}

function Stop-Services {
    Write-Host ($YELLOW + "[STOP] Stopping all .NET microservices..." + $NC)
    if (Test-Path $PidFile) {
        try {
            $pids = Get-Content $PidFile -Raw | ConvertFrom-Json
            foreach ($name in $pids.PSObject.Properties.Name) {
                $pidVal = $pids.$name
                if ($pidVal) {
                    $proc = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
                    if ($proc) {
                        Stop-Process -Id $pidVal -Force -ErrorAction SilentlyContinue
                        Write-Host "   Stopped $name (PID: $pidVal)"
                    }
                }
            }
        } catch {
            Write-Host ($RED + "   Failed to parse or stop from $PidFile" + $NC)
        }
        Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
    }

    Stop-Ports
    Write-Host ($GREEN + "[OK] All .NET services stopped." + $NC)
    Write-Host ($YELLOW + "   Note: PostgreSQL and Redis containers left running." + $NC)
}

function Show-Status {
    Write-Host ($CYAN + "[STATUS] WarpTalk Service Status:" + $NC)

    # Postgres
    $pgStatus = docker ps --filter "name=^/$PGContainer$" --format "{{.Status}}"
    if ($pgStatus) {
        Write-Host ("   " + $GREEN + "[OK] PostgreSQL (Docker: $PGContainer) - $pgStatus" + $NC)
    } else {
        Write-Host ("   " + $RED + "[FAIL] PostgreSQL (Docker: $PGContainer) - stopped" + $NC)
    }

    # Redis
    $redisStatus = docker ps --filter "name=^/$RedisContainer$" --format "{{.Status}}"
    if ($redisStatus) {
        Write-Host ("   " + $GREEN + "[OK] Redis (Docker: $RedisContainer) - $redisStatus" + $NC)
    } else {
        Write-Host ("   " + $RED + "[FAIL] Redis (Docker: $RedisContainer) - stopped" + $NC)
    }

    # RabbitMQ
    $rabbitStatus = docker ps --filter "name=^/$RabbitContainer$" --format "{{.Status}}"
    if ($rabbitStatus) {
        Write-Host ("   " + $GREEN + "[OK] RabbitMQ (Docker: $RabbitContainer) - $rabbitStatus" + $NC)
    } else {
        Write-Host ("   " + $RED + "[FAIL] RabbitMQ (Docker: $RabbitContainer) - stopped" + $NC)
    }

    # LiveKit
    $livekitStatus = docker ps --filter "name=^/$LiveKitContainer$" --format "{{.Status}}"
    if ($livekitStatus) {
        Write-Host ("   " + $GREEN + "[OK] LiveKit (Docker: $LiveKitContainer) - $livekitStatus" + $NC)
    } else {
        Write-Host ("   " + $RED + "[FAIL] LiveKit (Docker: $LiveKitContainer) - stopped" + $NC)
    }

    # microservices
    if (Test-Path $PidFile) {
        try {
            $pids = Get-Content $PidFile -Raw | ConvertFrom-Json
            foreach ($service in $Services) {
                $name = $service.Name
                $port = $service.Port
                $pidVal = $pids.$name

                if ($pidVal) {
                    $proc = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
                    if ($proc) {
                        Write-Host ("   " + $GREEN + "[OK] $name (PID: $pidVal, port: $port)" + $NC)
                    } else {
                        Write-Host ("   " + $RED + "[FAIL] $name (dead PID: $pidVal, port: $port)" + $NC)
                    }
                } else {
                    Write-Host ("   " + $YELLOW + "[OFF] $name (no active PID, port: $port)" + $NC)
                }
            }
        } catch {
            Write-Host ($RED + "   Failed to read $PidFile" + $NC)
        }
    } else {
        Write-Host ($YELLOW + "   [OFF] No active .NET services tracking file found." + $NC)
    }
}

function Wait-And-Test {
    Write-Host ""
    Write-Host ($YELLOW + "[WAIT] Waiting 8s for all services to initialize..." + $NC)
    Start-Sleep -Seconds 8

    Write-Host ($CYAN + "[CHECK] Service health check:" + $NC)

    if (Test-Path $PidFile) {
        $pids = Get-Content $PidFile -Raw | ConvertFrom-Json
        foreach ($service in $Services) {
            $name = $service.Name
            $port = $service.Port
            $pidVal = $pids.$name

            if ($pidVal) {
                $proc = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
                if ($proc) {
                    Write-Host ("   " + $GREEN + "[OK] $name (PID: $pidVal, port: $port)" + $NC)
                } else {
                    Write-Host ("   " + $RED + "[FAIL] $name - process died! Check: logs/$name.err" + $NC)
                }
            }
        }
    }

    Write-Host ""
    Write-Host ($CYAN + "[TEST] Testing Gateway health endpoint..." + $NC)
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:5200/health" -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        if ($resp.StatusCode -eq 200) {
            Write-Host ("   " + $GREEN + "[OK] Gateway /health -> 200 OK" + $NC)
        } else {
            Write-Host ("   " + $YELLOW + "[WARN] Gateway /health -> HTTP " + $resp.StatusCode + $NC)
        }
    } catch {
        Write-Host ("   " + $RED + "[FAIL] Gateway is unreachable" + $NC)
    }

    Write-Host ($CYAN + "[TEST] Hub endpoints checks:" + $NC)
    foreach ($hub in @("translation-room", "notification")) {
        try {
            $hubResp = Invoke-WebRequest -Method Post -Uri "http://localhost:5200/hubs/$hub/negotiate?negotiateVersion=1" -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
            if ($hubResp.StatusCode -eq 401) {
                Write-Host ("   " + $GREEN + "[OK] /hubs/$hub/negotiate -> 401 Unauthorized (JWT required, correct!)" + $NC)
            } else {
                Write-Host ("   " + $YELLOW + "[WARN] /hubs/$hub/negotiate -> HTTP " + $hubResp.StatusCode + $NC)
            }
        } catch {
            if ($_.Exception.Response.StatusCode -eq 401) {
                Write-Host ("   " + $GREEN + "[OK] /hubs/$hub/negotiate -> 401 Unauthorized (JWT required, correct!)" + $NC)
            } else {
                Write-Host ("   " + $RED + "[FAIL] Hub $hub check failed: " + $_.Exception.Message + $NC)
            }
        }
    }

    Write-Host ""
    Write-Host ($CYAN + "[INFO] Useful commands:" + $NC)
    Write-Host ("   View logs:  " + $YELLOW + "Get-Content logs/*.log -Tail 20 -Wait" + $NC)
    Write-Host ("   Stop all:   " + $YELLOW + ".\run-all-local.ps1 -Stop" + $NC)
    Write-Host ("   Status:     " + $YELLOW + ".\run-all-local.ps1 -Status" + $NC)
}

# --- Main Execution ---------------------------------------------------

if ($Stop) {
    Stop-Services
    exit 0
}

if ($Status) {
    Show-Status
    exit 0
}

Show-Banner
Stop-Ports

Start-Postgres
Invoke-Migrations
Invoke-Seeds
Start-Redis
Start-RabbitMQ
Start-LiveKit

# Rebuild Solution
Write-Host ($YELLOW + "[BUILD] Building all projects before starting..." + $NC)
dotnet build "$ScriptDir\warptalk-backend.slnx" -v m
Write-Host ($GREEN + "[OK] Build completed." + $NC)
Write-Host ""

# Ensure logs dir exists
if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir | Out-Null
}

$PidStore = @{}

# Start microservices
foreach ($service in $Services) {
    $name = $service.Name
    $cwd = $service.Cwd
    $port = $service.Port
    $fullPath = Join-Path $ScriptDir $cwd

    if (-not (Test-Path $fullPath)) {
        Write-Host ($YELLOW + "   [WARN] Skip $name - folder not found" + $NC)
        continue
    }

    Write-Host ("[START] Starting " + $CYAN + $name + $NC + "...")

    $stdoutFile = Join-Path $LogDir "$name.log"
    $stderrFile = Join-Path $LogDir "$name.err"

    # Temporarily set environmental variables for child processes to inherit
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ConnectionStrings__AuthDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=auth,public"
    $env:ConnectionStrings__MeetingDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=meeting,public"
    $env:ConnectionStrings__TranslationRoomDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=translation_room,public"
    $env:ConnectionStrings__TranscriptDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=transcript,public"
    $env:ConnectionStrings__WorkspaceDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=workspace,public"
    $env:ConnectionStrings__BillingDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=subscription,public"
    $env:ConnectionStrings__NotificationDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=platform,public"
    $env:ConnectionStrings__AssistantDb = "Host=localhost;Port=5432;Database=$env:POSTGRES_DB;Username=$env:POSTGRES_USER;Password=$env:POSTGRES_PASSWORD;Search Path=assistant,public"

    if ($name -eq "gateway") {
        $env:ASPNETCORE_URLS = "http://localhost:5200"
    }

    $proc = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList "run --no-build --no-launch-profile" `
        -WorkingDirectory $fullPath `
        -NoNewWindow `
        -RedirectStandardOutput $stdoutFile `
        -RedirectStandardError $stderrFile `
        -PassThru

    # Restore variables
    Remove-Item env:ASPNETCORE_ENVIRONMENT
    Remove-Item env:ConnectionStrings__AuthDb
    Remove-Item env:ConnectionStrings__MeetingDb
    Remove-Item env:ConnectionStrings__TranslationRoomDb
    Remove-Item env:ConnectionStrings__TranscriptDb
    Remove-Item env:ConnectionStrings__WorkspaceDb
    Remove-Item env:ConnectionStrings__BillingDb
    Remove-Item env:ConnectionStrings__NotificationDb
    Remove-Item env:ConnectionStrings__AssistantDb
    if ($name -eq "gateway") {
        Remove-Item env:ASPNETCORE_URLS
    }

    $PidStore[$name] = $proc.Id
    Write-Host ("   " + $GREEN + "PID: " + $proc.Id + " -> logs in logs/$name.log" + $NC)

    # Delay for startup order sequence
    if ($name -eq "auth") {
        Start-Sleep -Seconds 3
    } else {
        Start-Sleep -Seconds 1
    }
}

# Save PIDs
$PidStore | ConvertTo-Json | Out-File $PidFile -Force

Wait-And-Test

# Keep script alive to preserve child processes on Windows background runners
Write-Host ""
Write-Host ($GREEN + "[KEEP-ALIVE] Backend services are active. Keeping runner alive to prevent child process termination..." + $NC)
Write-Host "To stop services, run: .\run-all-local.ps1 -Stop"
while ($true) {
    Start-Sleep -Seconds 5
}
