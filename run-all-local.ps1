# ====================================================================
# WarpTalk Backend - Run All .NET Services Locally (no Docker)
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
dotnet build "$ScriptDir\warptalk-backend.slnx" -v m
Write-Host ($GREEN + "[OK] Build completed." + $NC)
Write-Host ""

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
}
