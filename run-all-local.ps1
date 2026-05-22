# ====================================================================
# WarpTalk Backend - Run All .NET Services Locally (no Docker)
<<<<<<< HEAD
# Manages PostgreSQL + Redis in Docker, and all .NET services natively.
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
    [PSCustomObject]@{ Name = "translation-room";  Cwd = "translation-room/src/WarpTalk.TranslationRoomService.API"; Port = 5102 },
    [PSCustomObject]@{ Name = "transcript";       Cwd = "transcript/src/WarpTalk.TranscriptService.API";       Port = 5103 },
    [PSCustomObject]@{ Name = "notification";     Cwd = "notification/src/WarpTalk.NotificationService.API";     Port = 5104 },
    [PSCustomObject]@{ Name = "meeting";          Cwd = "meeting/src/WarpTalk.MeetingService.API";          Port = 5105 },
    [PSCustomObject]@{ Name = "gateway";          Cwd = "gateway/src/WarpTalk.Gateway";                  Port = 5200 }
)

function Show-Banner {
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ($CYAN + "|             *** WarpTalk Backend Stack ***           |" + $NC)
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ($CYAN + "|  PostgreSQL  (Docker)         -> localhost:5432      |" + $NC)
    Write-Host ($CYAN + "|  Redis       (Docker)         -> localhost:6379      |" + $NC)
    Write-Host ($CYAN + "|  Auth        (REST+gRPC)      -> :5101 / :50051      |" + $NC)
    Write-Host ($CYAN + "|  Translation (REST+gRPC)      -> :5102 / :50052      |" + $NC)
    Write-Host ($CYAN + "|  Transcript  (REST+gRPC)      -> :5103 / :50053      |" + $NC)
    Write-Host ($CYAN + "|  Notification(REST+gRPC)      -> :5104 / :50054      |" + $NC)
    Write-Host ($CYAN + "|  Meeting     (REST+gRPC)      -> :5105 / :50055      |" + $NC)
    Write-Host ($CYAN + "|  Gateway     (YARP+SignalR)   -> :5200               |" + $NC)
    Write-Host ($CYAN + "+------------------------------------------------------+" + $NC)
    Write-Host ""
}

function Kill-Ports {
    Write-Host ($YELLOW + "[CLEAN] Cleaning up occupied ports..." + $NC)
    $ports = @(5101, 5102, 5103, 5104, 5105, 5200, 50051, 50052, 50053, 50054, 50055)
=======
# Just runs all dotnet services concurrently. Ctrl+C to stop all.
# Usage: .\run-all-local.ps1
# ====================================================================

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$ESC    = [char]27
$GREEN  = "$ESC[0;32m"
$CYAN   = "$ESC[0;36m"
$YELLOW = "$ESC[1;33m"
$NC     = "$ESC[0m"

$Jobs = @()

function Kill-Ports {
    Write-Host ($YELLOW + "[*] Cleaning up occupied ports..." + $NC)
    $ports = @(5001, 5242, 5214, 5209, 5201, 5105, 5200, 50051, 50052, 50053, 50054, 50055, 50056)
>>>>>>> development
    foreach ($port in $ports) {
        $connections = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
        foreach ($conn in $connections) {
            $procId = $conn.OwningProcess
            if ($procId -and $procId -ne 0) {
                try {
                    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                    Write-Host "   Killed process $procId on port $port"
                } catch {}
            }
        }
    }

<<<<<<< HEAD
    Write-Host ($YELLOW + "[CLEAN] Cleaning up lingering dotnet processes..." + $NC)
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    try { dotnet build-server shutdown 2>$null } catch {}
}

function Start-Postgres {
    Write-Host ($CYAN + "[DB] Starting PostgreSQL..." + $NC)
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
            -e POSTGRES_DB=warptalk `
            -e POSTGRES_USER=postgres `
            -e POSTGRES_PASSWORD=postgres `
            -p 5432:5432 `
            postgres:18-alpine | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }

    # Wait until healthy
    Write-Host -NoNewline "   Waiting for PostgreSQL to be ready"
    for ($i = 1; $i -le 30; $i++) {
        $ready = docker exec $PGContainer pg_isready -U postgres 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host ($GREEN + " [OK]" + $NC)
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
            redis:7-alpine redis-server --appendonly yes | Out-Null
        Write-Host ($GREEN + "   Created and started new container" + $NC)
    }

    # Wait until healthy
    Write-Host -NoNewline "   Waiting for Redis to be ready"
    for ($i = 1; $i -le 30; $i++) {
        $ping = docker exec $RedisContainer redis-cli ping 2>$null
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

function Run-Migrations {
    Write-Host ($CYAN + "[DB] Running PostgreSQL migrations..." + $NC)
    $MigrationsDir = Join-Path $ScriptDir "..\warptalk-infrastructure\scripts\migrations"
    if (Test-Path $MigrationsDir) {
        $files = Get-ChildItem -Path $MigrationsDir -Filter "*.sql" | Sort-Object Name
        foreach ($file in $files) {
            Write-Host "   Executing $($file.Name)..."
            Get-Content $file.FullName -Raw | docker exec -i $PGContainer psql -U postgres -d warptalk | Out-Null
        }
        Write-Host ($GREEN + "   [OK] Migrations completed" + $NC)
    } else {
        Write-Host ($YELLOW + "   [WARN] No migrations directory found at $MigrationsDir" + $NC)
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

    Kill-Ports
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
    $allOk = $true

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
                    $allOk = $false
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
Kill-Ports

Start-Postgres
Run-Migrations
Start-Redis

# Rebuild Solution
Write-Host ($YELLOW + "[BUILD] Building all projects before starting..." + $NC)
=======
    Write-Host ($YELLOW + "[*] Cleaning up lingering dotnet processes..." + $NC)
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch {}
    }

    try { dotnet build-server shutdown 2>$null } catch {}
}

function Stop-AllJobs {
    Write-Host ""
    Write-Host ($YELLOW + "Stopping all services..." + $NC)
    foreach ($job in $Jobs) {
        try { Stop-Job -Job $job -ErrorAction SilentlyContinue } catch {}
        try { Remove-Job -Job $job -Force -ErrorAction SilentlyContinue } catch {}
    }
    Write-Host ($GREEN + "All services stopped." + $NC)
}

# Ctrl+C / exit handler
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Stop-AllJobs }

Write-Host ($CYAN + "============================================" + $NC)
Write-Host ($CYAN + "   WarpTalk Backend - All Services Local    " + $NC)
Write-Host ($CYAN + "============================================" + $NC)
Write-Host ""

Kill-Ports
Write-Host ""

$Services = @(
    "auth/src/WarpTalk.AuthService.API|Auth|5001",
    "translation-room/src/WarpTalk.TranslationRoomService.API|TranslationRoom|5242",
    "transcript/src/WarpTalk.TranscriptService.API|Transcript|5214",
    "notification/src/WarpTalk.NotificationService.API|Notification|5209",
    "billing/src/WarpTalk.BillingService.API|Billing|5201",
    "meeting/src/WarpTalk.MeetingService.API|Meeting|5105",
    "gateway/src/WarpTalk.Gateway|Gateway|5200"
)

Write-Host ($YELLOW + "[~] Building all projects before starting..." + $NC)
>>>>>>> development
dotnet build "$ScriptDir\warptalk-backend.slnx" -v m
Write-Host ($GREEN + "[OK] Build completed." + $NC)
Write-Host ""

<<<<<<< HEAD
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
=======
foreach ($entry in $Services) {
    $parts    = $entry -split '\|'
    $project  = $parts[0]
    $name     = $parts[1]
    $port     = $parts[2]
    $fullPath = Join-Path $ScriptDir $project

    if (-not (Test-Path $fullPath)) {
        Write-Host ("  " + $YELLOW + "Skip $name - not found" + $NC)
        continue
    }

    Write-Host ("  " + $GREEN + ">> " + $NC + "$name -> http://localhost:$port")

    $job = Start-Job -ScriptBlock {
        param($projPath)
        dotnet run --no-build --launch-profile "http" --project $projPath
    } -ArgumentList $fullPath

    $Jobs += $job
}

Write-Host ""
Write-Host ($GREEN + "All services started. Press Ctrl+C to stop all." + $NC)
Write-Host ""

try {
    while ($true) {
        foreach ($job in $Jobs) {
            $output = Receive-Job -Job $job -ErrorAction SilentlyContinue
            if ($output) { Write-Host $output }
        }
        Start-Sleep -Milliseconds 500
    }
} finally {
    Stop-AllJobs
>>>>>>> development
}
