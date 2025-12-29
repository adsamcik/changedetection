<#
.SYNOPSIS
    Runs tests with mandatory logging. ALL test execution MUST go through this script.

.DESCRIPTION
    This script wraps TUnit test execution to ensure consistent logging to test-output.log.
    LLMs and developers MUST use this script instead of calling dotnet run directly.

    LLM Response Caching:
    - By default, tests run in CacheOnly mode for both LLM and content responses
    - Tests use SQLite caches at tests/ChangeDetection.Tests/Llm/Cache/ and Scraping/Cache/
    - Cache miss will fail the test with a clear error message
    - Use -IncludeOllama to run in CacheFirst mode (calls real Ollama, caches responses)
    - Use -IncludeInternet to run in CacheFirst mode (fetches real URLs, caches content)

    Cache Population:
    - Run with -IncludeOllama -IncludeInternet when Ollama and internet are available
    - This populates the cache for offline CI/CD runs

.PARAMETER Filter
    TUnit treenode filter expression (e.g., "/*/*/*/*[ClassName=MyTests]")
    Simple patterns: use "*TestName*" for substring match

.PARAMETER NoBuild
    Skip building before running tests

.PARAMETER Project
    Test project path (defaults to tests/ChangeDetection.Tests)

.PARAMETER TailLines
    Number of lines to show in console (default: 50, use 0 for all)

.PARAMETER MaxParallel
    Maximum parallel tests to run (default: 8)

.PARAMETER IncludeOllama
    Run LLM tests in CacheFirst mode (calls real Ollama on cache miss).
    By default, tests run in CacheOnly mode using cached responses.

.PARAMETER IncludeInternet
    Run content fetching in CacheFirst mode (fetches real URLs on cache miss).
    By default, tests run in CacheOnly mode using cached content.

.EXAMPLE
    ./test.ps1 -Filter "*ContentEnricher*"

.EXAMPLE
    ./test.ps1 -NoBuild

.EXAMPLE
    ./test.ps1  # Runs all tests using cached LLM and content responses

.EXAMPLE
    ./test.ps1 -IncludeOllama  # Populate LLM cache when Ollama is running

.EXAMPLE
    ./test.ps1 -IncludeInternet  # Populate content cache from live URLs

.EXAMPLE
    ./test.ps1 -IncludeOllama -IncludeInternet  # Full cache refresh
#>

param(
    [string]$Filter = "",
    [switch]$NoBuild,
    [string]$Project = "tests/ChangeDetection.Tests",
    [int]$TailLines = 50,
    [int]$MaxParallel = 8,
    [switch]$IncludeOllama,
    [switch]$IncludeInternet
)

$ErrorActionPreference = "Continue"

# Set environment variable to control Ollama test skipping
# By default, skip Ollama tests unless -IncludeOllama is specified
if (-not $IncludeOllama) {
    $env:SKIP_OLLAMA_TESTS = "true"
} else {
    $env:SKIP_OLLAMA_TESTS = $null
}

# Set environment variable to control internet-dependent test skipping
# By default, skip internet tests unless -IncludeInternet is specified
if (-not $IncludeInternet) {
    $env:SKIP_INTERNET_TESTS = "true"
} else {
    $env:SKIP_INTERNET_TESTS = $null
}

# Ensure we're in repo root
$repoRoot = $PSScriptRoot
Push-Location $repoRoot

try {
    $logFile = Join-Path $repoRoot "test-output.log"
    $resultsDir = Join-Path $repoRoot "TestResults"

    # Build command for TUnit (uses dotnet run)
    $runArgs = @("run", "--project", $Project)
    
    if (-not $NoBuild) {
        # Build first
        Write-Host "Building..." -ForegroundColor Cyan
        & dotnet build $Project --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed!" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
    
    $runArgs += "--no-build"
    
    # Separator for TUnit arguments
    $runArgs += "--"
    $runArgs += "--maximum-parallel-tests", $MaxParallel
    $runArgs += "--results-directory", $resultsDir

    if ($Filter) {
        $runArgs += "--treenode-filter", $Filter
    }

    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Running TUnit tests with logging to: $logFile" -ForegroundColor Cyan
    Write-Host "Filter: $(if ($Filter) { $Filter } else { '(all tests)' })" -ForegroundColor Cyan
    if (-not $IncludeOllama) {
        Write-Host "LLM tests: CacheOnly mode (use -IncludeOllama for CacheFirst)" -ForegroundColor Yellow
    } else {
        Write-Host "LLM tests: CacheFirst mode (will call Ollama on cache miss)" -ForegroundColor Green
    }
    if (-not $IncludeInternet) {
        Write-Host "Content fetching: CacheOnly mode (use -IncludeInternet for CacheFirst)" -ForegroundColor Yellow
    } else {
        Write-Host "Content fetching: CacheFirst mode (will fetch URLs on cache miss)" -ForegroundColor Green
    }
    Write-Host "========================================" -ForegroundColor Cyan

    # Run with mandatory logging
    & dotnet @runArgs 2>&1 | Tee-Object -FilePath $logFile

    $exitCode = $LASTEXITCODE

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Test execution complete" -ForegroundColor Cyan
    Write-Host "Full log: $logFile" -ForegroundColor Cyan
    Write-Host "TRX results: $resultsDir" -ForegroundColor Cyan
    Write-Host "Exit code: $exitCode" -ForegroundColor $(if ($exitCode -eq 0) { "Green" } else { "Red" })
    Write-Host "========================================" -ForegroundColor Cyan

    if ($TailLines -gt 0) {
        Write-Host ""
        Write-Host "Last $TailLines lines of output:" -ForegroundColor Yellow
        Get-Content $logFile -Tail $TailLines
    }

    exit $exitCode
}
finally {
    Pop-Location
}
