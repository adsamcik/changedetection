#!/usr/bin/env pwsh
cd G:\Github\changedetection
Write-Host "Starting search tests..."
$startTime = Get-Date
dotnet run --project tests/ChangeDetection.Tests --no-build -- --treenode-filter "*/ChangeDetection.Tests.Search/*" 2>&1 | Out-File G:\Github\changedetection\search-test-run.log -Force
Write-Host "Test run completed at $(Get-Date). Duration: $((Get-Date) - $startTime)"
Write-Host "`nLast 50 lines of output:"
Get-Content G:\Github\changedetection\search-test-run.log -Tail 50
