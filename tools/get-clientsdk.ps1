<#
.SYNOPSIS
    Готовит сборку без своего GitLab: скачивает пакет Pilot.ClientSdk из релиза GitHub и пишет nuget.local.config.

.DESCRIPTION
    Reports ссылается на NuGet-пакет Pilot.ClientSdk. На nuget.org его нет: в GitLab стенда он лежит в реестре пакетов
    (источник cicd в nuget.config), а для всех остальных прикладывается к релизам github.com/PilotOpenTools/PilotClientSdk.
    Скрипт кладёт пакет в local-pkg/ и пишет nuget.local.config, в котором источник cicd указывает на эту папку.
    Если релиза с версией из Reports.csproj нет, берётся последний релиз: NuGet выберет ближайшую большую версию (NU1603).

.EXAMPLE
    ./tools/get-clientsdk.ps1
    dotnet build Reports.sln -c Release -p:RestoreConfigFile=nuget.local.config
#>
[CmdletBinding()]
param(
    [string]$Repository = 'PilotOpenTools/PilotClientSdk',
    # Версия пакета; по умолчанию - из PackageReference в Reports.csproj
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $project = [xml](Get-Content -LiteralPath (Join-Path $root 'Reports.csproj') -Raw)
    $Version = $project.SelectSingleNode("//PackageReference[@Include='Pilot.ClientSdk']").Version
}
$feed = Join-Path $root 'local-pkg'
New-Item -ItemType Directory -Force $feed | Out-Null

$headers = @{ 'User-Agent' = 'PilotReports-build'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }
$api = "https://api.github.com/repos/$Repository/releases"
try {
    $release = Invoke-RestMethod -Uri "$api/tags/v$Version" -Headers $headers
}
catch {
    Write-Warning "Нет релиза v$Version в $Repository, беру последний"
    $release = Invoke-RestMethod -Uri "$api/latest" -Headers $headers
}
$asset = $release.assets | Where-Object { $_.name -like 'Pilot.ClientSdk.*.nupkg' } | Select-Object -First 1
if (-not $asset) { throw "В релизе $($release.tag_name) нет пакета Pilot.ClientSdk" }
$target = Join-Path $feed $asset.name
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing -Headers @{ 'User-Agent' = 'PilotReports-build' }
Write-Host "Pilot.ClientSdk: $($asset.name) из $($release.html_url)"

$config = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Создан tools/get-clientsdk.ps1: источник cicd - локальная папка local-pkg вместо реестра GitLab -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="cicd" value="local-pkg" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="cicd">
      <package pattern="Pilot.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
Set-Content -LiteralPath (Join-Path $root 'nuget.local.config') -Value $config -Encoding UTF8
Write-Host 'nuget.local.config: источник cicd = local-pkg'
