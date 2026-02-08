<#
.SYNOPSIS
    Run Playwright UI orchestration tests for ChangeDetection.

.DESCRIPTION
    Builds the UITests project, ensures Playwright browsers are installed,
    and runs Playwright-based UI tests with full output capture.

.PARAMETER Filter
    TUnit treenode filter expression (e.g., "*/*/DashboardTests/*").

.PARAMETER NoBuild
    Skip building before running tests.

.PARAMETER Headed
    Run Playwright with a visible browser window (for debugging).

.PARAMETER TailLines
    Number of lines to show from console output (default: 50, 0 = all).

.PARAMETER IncludeLlm
    Use CacheFirst mode for LLM responses (populate cache from LLM provider).

.PARAMETER IncludeInternet
    Use CacheFirst mode for content fetching (populate cache from internet).

.EXAMPLE
    ./test-ui.ps1                                  # Run all UI tests
    ./test-ui.ps1 -Filter "*/*/DashboardTests/*"   # Dashboard tests only
    ./test-ui.ps1 -Headed                          # Visible browser
    ./test-ui.ps1 -Headed -Filter "*SmartInput*"   # Debug specific test
#>
param(
    [string]$Filter = "",
    [switch]$NoBuild,
    [switch]$Headed,
    [int]$TailLines = 50,
    [switch]$IncludeLlm,
    [switch]$IncludeInternet
)

$ErrorActionPreference = "Stop"
$project = "tests/ChangeDetection.UITests"
$outputLog = "test-ui-output.log"

Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  ChangeDetection UI Orchestration Tests   " -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Set environment variables for caching
if (-not $IncludeLlm) {
    $env:SKIP_LLM_TESTS = "true"
    Write-Host "  LLM Cache: CacheOnly (use -IncludeLlm to populate)" -ForegroundColor DarkGray
} else {
    $env:SKIP_LLM_TESTS = $null
    Write-Host "  LLM Cache: CacheFirst (will call LLM on miss)" -ForegroundColor Yellow
}

if (-not $IncludeInternet) {
    $env:SKIP_INTERNET_TESTS = "true"
    Write-Host "  Content Cache: CacheOnly (use -IncludeInternet to populate)" -ForegroundColor DarkGray
} else {
    $env:SKIP_INTERNET_TESTS = $null
    Write-Host "  Content Cache: CacheFirst (will fetch URLs on miss)" -ForegroundColor Yellow
}

if ($Headed) {
    $env:HEADED = "true"
    Write-Host "  Browser: Headed (visible)" -ForegroundColor Yellow
} else {
    $env:HEADED = $null
    Write-Host "  Browser: Headless" -ForegroundColor DarkGray
}

Write-Host ""

# Ensure Playwright browsers are installed
Write-Host "Checking Playwright browsers..." -ForegroundColor DarkGray
$pwshPath = Join-Path (Resolve-Path $project) "bin" "Debug" "net10.0"

# Build first if needed (to get the Playwright assembly)
if (-not $NoBuild) {
    Write-Host "Building $project..." -ForegroundColor DarkGray
    dotnet build $project --nologo -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed! Running full build for details..." -ForegroundColor Red
        dotnet build $project 2>&1 | Out-File $outputLog
        Get-Content $outputLog -Tail 30
        exit 1
    }
    Write-Host "  Build succeeded" -ForegroundColor Green
}

# Install Playwright browsers
$playwrightPs1 = Get-ChildItem -Path $pwshPath -Filter "playwright.ps1" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($playwrightPs1) {
    Write-Host "Installing Playwright browsers (if needed)..." -ForegroundColor DarkGray
    & pwsh $playwrightPs1.FullName install chromium 2>&1 | Out-Null
    Write-Host "  Playwright browsers ready" -ForegroundColor Green
} else {
    Write-Host "  Warning: playwright.ps1 not found, browsers may not be installed" -ForegroundColor Yellow
}

Write-Host ""

# Build the test command
$testArgs = @(
    "run"
    "--project", $project
    "--no-build"
    "--"
    "--maximum-parallel-tests", "4"
)

if ($Filter) {
    $testArgs += "--treenode-filter"
    $testArgs += $Filter
    Write-Host "Filter: $Filter" -ForegroundColor Cyan
}

Write-Host "Running UI tests..." -ForegroundColor Cyan
Write-Host "Output → $outputLog" -ForegroundColor DarkGray
Write-Host ""

# Run tests with full output capture
& dotnet @testArgs 2>&1 | Tee-Object -FilePath $outputLog

$exitCode = $LASTEXITCODE

Write-Host ""
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

if ($exitCode -eq 0) {
    Write-Host "  ✅ All UI tests passed!" -ForegroundColor Green
} else {
    Write-Host "  ❌ Some tests failed (exit code: $exitCode)" -ForegroundColor Red
    Write-Host "  Full output: $outputLog" -ForegroundColor Yellow
    
    # Show screenshots if any
    $screenshotDir = Join-Path $pwshPath "Screenshots"
    if (Test-Path $screenshotDir) {
        $screenshots = Get-ChildItem $screenshotDir -Filter "*.png" | Sort-Object LastWriteTime -Descending | Select-Object -First 5
        if ($screenshots) {
            Write-Host ""
            Write-Host "  Screenshots captured:" -ForegroundColor Yellow
            foreach ($ss in $screenshots) {
                Write-Host "    $($ss.FullName)" -ForegroundColor DarkGray
            }
        }
    }
}

Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if ($TailLines -gt 0) {
    Write-Host "Last $TailLines lines of output:" -ForegroundColor DarkGray
    Get-Content $outputLog -Tail $TailLines
}

exit $exitCode
