<#
.SYNOPSIS
    Runs tests with mandatory logging. ALL test execution MUST go through this script.

.DESCRIPTION
    This script wraps dotnet test to ensure consistent logging to test-output.log.
    LLMs and developers MUST use this script instead of calling dotnet test directly.

.PARAMETER Filter
    Test filter expression (e.g., "FullyQualifiedName~MyTest" or "Category=Unit")

.PARAMETER NoBuild
    Skip building before running tests

.PARAMETER Project
    Test project path (defaults to tests/ChangeDetection.Tests)

.PARAMETER TailLines
    Number of lines to show in console (default: 50, use 0 for all)

.EXAMPLE
    ./test.ps1 -Filter "FullyQualifiedName~ContentEnricher"

.EXAMPLE
    ./test.ps1 -Filter "Category=Unit" -NoBuild

.EXAMPLE
    ./test.ps1  # Runs all tests
#>

param(
    [string]$Filter = "",
    [switch]$NoBuild,
    [string]$Project = "tests/ChangeDetection.Tests",
    [int]$TailLines = 50
)

$ErrorActionPreference = "Continue"

# Ensure we're in repo root
$repoRoot = $PSScriptRoot
Push-Location $repoRoot

try {
    $logFile = Join-Path $repoRoot "test-output.log"
    $resultsDir = Join-Path $repoRoot "TestResults"

    # Build command
    $args = @(
        "test"
        $Project
        "--logger", "trx;LogFileName=results.trx"
        "--logger", "console;verbosity=detailed"
        "--results-directory", $resultsDir
    )

    if ($Filter) {
        $args += "--filter", $Filter
    }

    if ($NoBuild) {
        $args += "--no-build"
    }

    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Running tests with logging to: $logFile" -ForegroundColor Cyan
    Write-Host "Filter: $(if ($Filter) { $Filter } else { '(all tests)' })" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    # Run with mandatory logging
    & dotnet @args 2>&1 | Tee-Object -FilePath $logFile

    $exitCode = $LASTEXITCODE

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Test execution complete" -ForegroundColor Cyan
    Write-Host "Full log: $logFile" -ForegroundColor Cyan
    Write-Host "TRX results: $resultsDir\results.trx" -ForegroundColor Cyan
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
