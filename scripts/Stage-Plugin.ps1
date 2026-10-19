param(
    [Parameter(Mandatory)][string]$PluginDirectory,
    [string]$TargetRuntime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repo 'src/Jellyfin.Plugin.DistributedHls') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$output = Join-Path $repo 'src/Jellyfin.Plugin.DistributedHls/bin/Release/net10.0'
New-Item -ItemType Directory -Path $PluginDirectory -Force | Out-Null
foreach ($name in @('Jellyfin.Plugin.DistributedHls.dll','DistributedHls.Core.dll','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.provider.e_sqlite3.dll')) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $PluginDirectory -Force
}
$native = Join-Path $output "runtimes/$TargetRuntime/native"
if (-not (Test-Path -LiteralPath $native)) { throw "SQLite native libraries are unavailable for $TargetRuntime. Specify -TargetRuntime, for example win-x64 or linux-x64." }
Get-ChildItem -LiteralPath $native -File | Copy-Item -Destination $PluginDirectory -Force
$manifest = @{ guid='1d46b74f-6daf-4c2c-a28f-52f202679c16'; name='Distributed HLS'; version='0.2.0.0'; targetAbi='13.0.0.0'; status='Active'; autoUpdate=$false; assemblies=@('Jellyfin.Plugin.DistributedHls.dll','DistributedHls.Core.dll') }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PluginDirectory 'meta.json') -Encoding utf8
Write-Output "Staged Distributed HLS at $PluginDirectory"
