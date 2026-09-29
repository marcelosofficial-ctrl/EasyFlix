[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repoRoot "src\EasyFlix.App\EasyFlix.App.csproj"

Write-Host ""
Write-Host "EasyFlix safe launcher" -ForegroundColor Cyan
Write-Host "Repository: $repoRoot"
Write-Host "Configuration: $Configuration"
Write-Host ""

$running = @(Get-Process -Name "EasyFlix" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    foreach ($process in $running) {
        Write-Host "Closing existing EasyFlix process PID $($process.Id)..." -ForegroundColor Yellow
        try {
            $null = $process.CloseMainWindow()
        }
        catch {
            # A process without a responsive main window will be handled below.
        }
    }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-Process -Name "EasyFlix" -ErrorAction SilentlyContinue)
    } while ($running.Count -gt 0 -and (Get-Date) -lt $deadline)

    if ($running.Count -gt 0) {
        foreach ($process in $running) {
            Write-Host "EasyFlix PID $($process.Id) did not close in time; stopping it so build files can be replaced." -ForegroundColor Yellow
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        Start-Sleep -Milliseconds 500
    }
}

$remaining = @(Get-Process -Name "EasyFlix" -ErrorAction SilentlyContinue)
if ($remaining.Count -gt 0) {
    throw "EasyFlix is still running and may lock build output."
}

Write-Host "No EasyFlix process is holding the build output." -ForegroundColor Green
Write-Host "Starting latest $Configuration build..." -ForegroundColor Green
Write-Host ""

Push-Location $repoRoot
try {
    & dotnet run --project $project -c $Configuration
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
