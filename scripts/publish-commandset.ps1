<#
.SYNOPSIS
    Builds the command set for each Revit year and publishes it to the shared
    MCP server's releases folder, from where the Revit plugins update
    themselves on their next start (see docs/shared-server-docker.md).

.EXAMPLE
    ./scripts/publish-commandset.ps1 -Years 2025,2026
    ./scripts/publish-commandset.ps1 -Years 2024 -Out \\mcp-server\revit-mcp\releases
#>
param(
    [ValidatePattern('^20\d\d$')]
    [string[]]$Years = @('2025', '2026'),
    [string]$Out,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'server/releases' }

$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = if (git -C $root status --porcelain -- commandset command.json) { '-dirty' } else { '' }
$version = (Get-Date -Format 'yyyy.MM.dd.HHmmss') + "-$commit$dirty"

foreach ($year in $Years) {
    $config = "$Configuration R$($year.Substring(2))"
    Write-Host "Building command set for Revit $year ($config)..." -ForegroundColor Cyan
    dotnet build "$root/commandset/RevitMCPCommandSet.csproj" -c $config --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $year" }

    $bin = Join-Path $root "commandset/bin/$config"
    $staging = Join-Path ([IO.Path]::GetTempPath()) "commandset-$year-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        # Same files the build deploys into the plugin's Commands folder
        Copy-Item "$bin/*.dll", "$bin/*.pdb" $staging
        Copy-Item "$root/command.json" $staging

        $yearDir = Join-Path $Out $year
        New-Item -ItemType Directory -Force -Path $yearDir | Out-Null
        $file = "commandset-$year-$version.zip"
        $zip = Join-Path $yearDir $file
        Compress-Archive -Path "$staging/*" -DestinationPath $zip -Force
    }
    finally {
        Remove-Item $staging -Recurse -Force
    }

    $sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        version = $version
        file    = $file
        sha256  = $sha256
        builtAt = (Get-Date).ToString('o')
    }
    # Write the manifest last: plugins only see the new version once the zip is complete
    $manifestPath = Join-Path $yearDir 'manifest.json'
    [IO.File]::WriteAllText("$manifestPath.tmp", ($manifest | ConvertTo-Json))
    Move-Item "$manifestPath.tmp" $manifestPath -Force

    # Keep the current and previous package, for plugins mid-download
    Get-ChildItem $yearDir -Filter 'commandset-*.zip' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 2 |
        Remove-Item -Force

    Write-Host "Published Revit $year command set $version -> $zip" -ForegroundColor Green
}

Write-Host ""
Write-Host "Employees get it the next time they start Revit and click 'Revit MCP Switch'."
