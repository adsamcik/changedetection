#!/bin/bash
# ============================================================================
# Dev Container Bootstrap Script
# Idempotent - safe to re-run multiple times
# ============================================================================

set -e

echo "========================================"
echo "ChangeDetection Dev Container Bootstrap"
echo "========================================"

# .NET restore
echo ""
echo ">>> Restoring .NET dependencies..."
if [ -f "ChangeDetection.slnx" ]; then
    dotnet restore ChangeDetection.slnx
elif ls *.sln 1> /dev/null 2>&1; then
    dotnet restore
else
    echo "No solution file found, restoring individual projects..."
    find . -name "*.csproj" -exec dotnet restore {} \;
fi

# Install Playwright browsers (required for browser-based scraping tests)
echo ""
echo ">>> Installing Playwright browsers..."
# Build first to ensure Playwright CLI is available
dotnet build src/ChangeDetection/ChangeDetection/ChangeDetection.csproj --no-restore -v q || true

# Install Playwright browsers using the installed package
if command -v pwsh &> /dev/null; then
    pwsh -Command "& { \$env:PLAYWRIGHT_BROWSERS_PATH='0'; dotnet tool install --global Microsoft.Playwright.CLI 2>/dev/null || true; playwright install chromium --with-deps 2>/dev/null || echo 'Playwright CLI install skipped - will install on first run' }"
else
    echo "PowerShell not available, Playwright browsers will be installed on first test run"
fi

echo ""
echo "========================================"
echo "Bootstrap complete!"
echo ""
echo "Next steps:"
echo "  1. Build:  dotnet build"
echo "  2. Test:   ./test.ps1"
echo "  3. Run:    dotnet run --project src/ChangeDetection/ChangeDetection"
echo "========================================"
