$env:SKIP_OLLAMA_TESTS = 'true'
$env:SKIP_INTERNET_TESTS = 'true'
cd 'G:\Github\changedetection'
dotnet run --project tests\ChangeDetection.Tests -- --treenode-filter "*/*/ShouldNotifyTests/*" 2>&1 | Tee-Object -FilePath test-suite1.log
$exitCode = $LASTEXITCODE
"" | Tee-Object -Append -FilePath test-suite1.log
"Exit Code: $exitCode" | Tee-Object -Append -FilePath test-suite1.log
exit $exitCode
