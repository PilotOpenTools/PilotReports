<#
  Загрузка сборок клиента Pilot (шесть dll АСКОН) в реестр пакетов GitLab проекта модуля - для сборки в CI.
  Запускать на машине с установленным клиентом Pilot-BIM. В репозитории этих dll нет: они проприетарные.
  Пакет: <GitLabUrl>/api/v4/projects/<ProjectId>/packages/generic/pilot-client/<Version>/pilot-client.zip;
  CI берёт его по версии из переменной PILOT_CLIENT_VERSION (по умолчанию 26.13.0).

  .\tools\publish-pilot-client.ps1 -GitLabUrl http://gitlab.example.local:8080 -ProjectId 9
  .\tools\publish-pilot-client.ps1 -GitLabUrl https://gitlab.example.local -ProjectId client-modules/module-for-report -Version 26.13.0 -ClientDir D:\Pilot-BIM

  Токен GitLab (права api, роль не ниже Developer) - в переменной окружения GITLAB_TOKEN или параметре -Token; в вывод не попадает.
  Версия по умолчанию - первые три числа версии Ascon.Pilot.Pilot.dll. Каталог клиента - -ClientDir, иначе PILOT_CLIENT_DIR,
  иначе C:\Program Files\ASCON\Pilot-BIM. Повторная загрузка той же версии добавляет файл: CI скачивает последний.
#>
param(
    [Parameter(Mandatory)][string]$GitLabUrl,
    [Parameter(Mandatory)][string]$ProjectId,
    [string]$Version,
    [string]$ClientDir,
    [string]$Token
)
$ErrorActionPreference = 'Stop'

$names = 'Pilot', 'ClientCore', 'Common', 'DataClasses', 'Core', 'ProjectionsRepository' | ForEach-Object { "Ascon.Pilot.$_.dll" }

if (-not $Token) { $Token = $env:GITLAB_TOKEN }
if (-not $Token) { throw 'Не задан токен GitLab: переменная окружения GITLAB_TOKEN или параметр -Token (права api).' }
if ($GitLabUrl -notmatch '^https?://[^/\s]+') { throw "Адрес GitLab должен начинаться с http:// или https://: $GitLabUrl" }
$GitLabUrl = $GitLabUrl.TrimEnd('/')
if ($GitLabUrl -like 'http://*') { Write-Warning 'GitLab по http: токен уходит по сети открытым текстом.' }

if (-not $ClientDir) { $ClientDir = $env:PILOT_CLIENT_DIR }
if (-not $ClientDir) { $ClientDir = 'C:\Program Files\ASCON\Pilot-BIM' }
if (-not (Test-Path -LiteralPath $ClientDir -PathType Container)) { throw "Нет каталога клиента Pilot: $ClientDir. Укажите -ClientDir или PILOT_CLIENT_DIR." }
$missing = $names | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ClientDir $_)) }
if ($missing) { throw "В $ClientDir нет файлов: $($missing -join ', ')." }

if (-not $Version) {
    $fileVersion = (Get-Item -LiteralPath (Join-Path $ClientDir $names[0])).VersionInfo.FileVersion
    if ($fileVersion -notmatch '^(\d+\.\d+\.\d+)') { throw "Не удалось определить версию клиента по $($names[0]) ($fileVersion): укажите -Version." }
    $Version = $Matches[1]
}
if ($Version -notmatch '^\d+(\.\d+){2,3}$') { throw "Версия должна быть вида 26.13.0 (три или четыре числа): $Version" }

$uri = "$GitLabUrl/api/v4/projects/$([uri]::EscapeDataString($ProjectId))/packages/generic/pilot-client/$Version/pilot-client.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pilot-client-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'pilot-client.zip'
    Compress-Archive -LiteralPath ($names | ForEach-Object { Join-Path $ClientDir $_ }) -DestinationPath $zip
    try {
        Invoke-RestMethod -Method Put -Uri $uri -Headers @{ 'PRIVATE-TOKEN' = $Token } -InFile $zip -ContentType 'application/zip' | Out-Null
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $hint = switch ($code) {
            401 { 'токен не принят' }
            403 { 'у токена нет прав на запись пакетов (нужны api и роль Developer)' }
            404 { 'проект не найден или токен его не видит: проверьте -ProjectId и -GitLabUrl' }
            0   { "GitLab недоступен: $($_.Exception.Message)" }
            default { $_.Exception.Message }
        }
        throw "Загрузка не удалась (HTTP $code): $hint"
    }
    Write-Host "Загружено: $uri ($($names.Count) файлов, версия клиента $Version, $((Get-Item -LiteralPath $zip).Length) байт)."
    Write-Host "Если эта версия не совпадает с PILOT_CLIENT_VERSION в .gitlab-ci.yml, задайте PILOT_CLIENT_VERSION=$Version в переменных CI/CD."
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
