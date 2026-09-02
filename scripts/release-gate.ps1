<#
.SYNOPSIS
    Builds and validates a ChangeDetection release candidate.

.DESCRIPTION
    Runs the repository's complete cache-backed test surface, audits NuGet
    dependencies, publishes the application, produces checksums, and validates
    the production container with its health check.

.PARAMETER Version
    Semantic version embedded in the publish output and container metadata.

.PARAMETER SkipContainer
    Skips the container build and smoke test. This produces a limited preflight,
    not a complete release-gate pass.

.PARAMETER AllowDirty
    Allows uncommitted files or changes. Intended only for development diagnosis.
#>

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.0.0-local',
    [switch]$SkipContainer,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'ChangeDetection.slnx'
$artifactRoot = Join-Path $repoRoot '.artifacts/release-gate'
$publishDirectory = Join-Path $artifactRoot 'publish'
$testScript = Join-Path $repoRoot 'test.ps1'
$appProject = Join-Path $repoRoot 'src/ChangeDetection/ChangeDetection/ChangeDetection.csproj'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter()] [string[]]$Arguments = @()
    )

    Write-Host ""
    Write-Host "==> $FilePath $($Arguments -join ' ')" -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

foreach ($tool in @('dotnet', 'git', 'pwsh')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required tool '$tool' was not found on PATH."
    }
}

if (-not $SkipContainer -and -not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker was not found on PATH. Use -SkipContainer only for a limited preflight."
}

Push-Location $repoRoot
try {
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the Git revision.'
    }

    & git diff --quiet --ignore-submodules --
    $workingTreeDirty = $LASTEXITCODE -ne 0
    & git diff --cached --quiet --ignore-submodules --
    $indexDirty = $LASTEXITCODE -ne 0
    $untrackedFiles = @(& git ls-files --others --exclude-standard)
    if (($workingTreeDirty -or $indexDirty -or $untrackedFiles.Count -gt 0) -and -not $AllowDirty) {
        throw 'Uncommitted files or changes are present. Commit them or use -AllowDirty for a non-certifying development run.'
    }

    if (-not $SkipContainer) {
        Invoke-Checked docker @('version', '--format', '{{.Server.Version}}')
    }

    $artifactFullPath = [IO.Path]::GetFullPath($artifactRoot)
    $repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $artifactFullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean artifact path outside the repository: $artifactFullPath"
    }

    if (Test-Path -LiteralPath $artifactFullPath) {
        Remove-Item -LiteralPath $artifactFullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    Invoke-Checked dotnet @('restore', $solutionPath)
    Invoke-Checked dotnet @(
        'build', $solutionPath,
        '--configuration', 'Release',
        '--no-restore',
        '-p:RunAOTCompilation=false',
        "-p:Version=$Version",
        "-p:SourceRevisionId=$revision",
        '-p:ContinuousIntegrationBuild=true'
    )

    $playwrightInstaller = Join-Path $repoRoot 'tests/ChangeDetection.Tests/bin/Release/net10.0/playwright.ps1'
    if (-not (Test-Path -LiteralPath $playwrightInstaller)) {
        throw "Playwright installer was not produced at $playwrightInstaller"
    }
    Invoke-Checked pwsh @('-NoProfile', '-File', $playwrightInstaller, 'install', '--with-deps', 'chromium')

    foreach ($project in @(
        'tests/ChangeDetection.Tests',
        'tests/ChangeDetection.Core.Tests',
        'tests/ChangeDetection.UITests'
    )) {
        $testArguments = @(
            '-NoProfile', '-File', $testScript,
            '-Project', $project,
            '-Configuration', 'Release',
            '-NoBuild',
            '-TailLines', '0'
        )
        if ($project -eq 'tests/ChangeDetection.UITests') {
            $testArguments += @('-MaxParallel', '1')
        }
        Invoke-Checked pwsh $testArguments
    }

    Write-Host ""
    Write-Host '==> Auditing NuGet dependencies' -ForegroundColor Cyan
    $auditLines = @(& dotnet list $solutionPath package --vulnerable --include-transitive --format json --output-version 1 --no-restore 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $auditLines | ForEach-Object { Write-Host $_ }
        throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE."
    }
    $auditText = $auditLines -join [Environment]::NewLine
    $audit = $auditText | ConvertFrom-Json
    if ($null -eq $audit.projects) {
        throw 'NuGet vulnerability audit returned an unexpected response.'
    }
    $vulnerabilityCount = [regex]::Matches($auditText, '"severity"\s*:').Count
    if ($vulnerabilityCount -ne 0) {
        $auditLines | ForEach-Object { Write-Host $_ }
        throw "NuGet reported $vulnerabilityCount vulnerable package entr$(if ($vulnerabilityCount -eq 1) { 'y' } else { 'ies' })."
    }
    Write-Host "NuGet audit passed for $(@($audit.projects).Count) projects." -ForegroundColor Green

    Invoke-Checked dotnet @(
        'publish', $appProject,
        '--configuration', 'Release',
        '--no-build',
        '--output', $publishDirectory,
        '-p:RunAOTCompilation=false',
        "-p:Version=$Version",
        "-p:SourceRevisionId=$revision",
        '-p:ContinuousIntegrationBuild=true'
    )

    $releaseMetadata = [ordered]@{
        version = $Version
        revision = $revision
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        framework = 'net10.0'
        containerValidated = -not $SkipContainer
    }
    $metadataPath = Join-Path $artifactRoot 'release.json'
    $releaseMetadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

    $checksumPath = Join-Path $artifactRoot 'SHA256SUMS'
    $hashLines = Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object { $_.FullName -ne $checksumPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($artifactRoot, $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    $hashLines | Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM

    if (-not $SkipContainer) {
        $safeVersion = $Version.ToLowerInvariant()
        $imageName = "changedetection:release-gate-$safeVersion"
        $containerName = "changedetection-release-gate-$PID"

        Invoke-Checked docker @(
            'build',
            '--file', (Join-Path $repoRoot 'Dockerfile'),
            '--tag', $imageName,
            '--build-arg', "VERSION=$Version",
            '--build-arg', "VCS_REF=$revision",
            $repoRoot
        )

        try {
            Invoke-Checked docker @('run', '--detach', '--name', $containerName, $imageName)

            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
            $health = 'starting'
            while ([DateTimeOffset]::UtcNow -lt $deadline) {
                $health = (& docker inspect --format '{{.State.Health.Status}}' $containerName).Trim()
                if ($LASTEXITCODE -ne 0) {
                    throw 'Unable to inspect the release-gate container.'
                }
                if ($health -eq 'healthy') {
                    break
                }
                if ($health -eq 'unhealthy') {
                    break
                }
                Start-Sleep -Seconds 2
            }

            if ($health -ne 'healthy') {
                & docker logs $containerName 2>&1 | ForEach-Object { Write-Host $_ }
                throw "Container health check finished with status '$health'."
            }
            Write-Host "Container $imageName is healthy." -ForegroundColor Green
        }
        finally {
            & docker rm --force --volumes $containerName 2>$null | Out-Null
        }
    }

    Write-Host ""
    Write-Host "Release gate passed for $Version at $revision." -ForegroundColor Green
    Write-Host "Artifacts: $artifactRoot" -ForegroundColor Green
    if ($SkipContainer) {
        Write-Warning 'Container validation was skipped; this was not a complete release-gate pass.'
    }
}
finally {
    Pop-Location
}
