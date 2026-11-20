<#
.SYNOPSIS
    Скачивает дистрибутив клиента Pilot-BIM из центра загрузок АСКОН и кладёт в lib/pilot шесть сборок для компиляции.

.DESCRIPTION
    Типов отчётов (ReportContext, RObject, QueryBuilder...) нет в NuGet-пакете Ascon.Pilot.SDK, они лежат в сборках клиента.
    Сами сборки проприетарные и в репозиторий не входят. Скрипт берёт их из публичного дистрибутива:

      https://pilot.ascon.ru/release/Pilot-BIM-setup_<версия>.zip  ->  Pilot-BIM-setup.exe (WiX Burn)
      wix burn extract  ->  MSI клиента  ->  msiexec /a (административная распаковка, без установки)

    Нужен Windows (msiexec) и .NET SDK (для инструмента wix). Пилот не устанавливается, права администратора не нужны.
    Используется в GitHub Actions; на машине с установленным Pilot-BIM скрипт не нужен (PilotClient.props возьмёт сборки оттуда).

.EXAMPLE
    ./tools/get-pilot-client.ps1                          # версия по умолчанию
    ./tools/get-pilot-client.ps1 -Version 26.19.0.65157   # другая версия клиента
#>
[CmdletBinding()]
param(
    # Полная версия из имени файла в центре загрузок (четыре числа)
    [string]$Version = '26.13.0.62219',
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'lib/pilot'),
    [string]$BaseUrl = 'https://pilot.ascon.ru/release/',
    # Версия WiX, у которой есть "wix burn extract"
    [string]$WixVersion = '5.0.2'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$assemblies = 'Ascon.Pilot.Pilot', 'Ascon.Pilot.ClientCore', 'Ascon.Pilot.Common', 'Ascon.Pilot.DataClasses', 'Ascon.Pilot.Core', 'Ascon.Pilot.ProjectionsRepository'

$missing = $assemblies | Where-Object { -not (Test-Path (Join-Path $Destination "$_.dll")) }
$stamp = Join-Path $Destination 'VERSION.txt'
if (-not $missing -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $Version) {
    Write-Host "Сборки клиента $Version уже в $Destination"
    return
}

$work = Join-Path ([IO.Path]::GetTempPath()) "pilot-client-$Version"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $zip = Join-Path $work 'setup.zip'
    $url = "$BaseUrl" + "Pilot-BIM-setup_$Version.zip"
    Write-Host "Скачиваю $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath "$work\setup"
    $exe = Get-ChildItem "$work\setup" -Filter '*.exe' -Recurse | Select-Object -First 1
    if (-not $exe) { throw "В $url нет установщика .exe" }

    $tools = Join-Path $work 'tools'
    & dotnet tool install wix --version $WixVersion --tool-path $tools | Out-Host
    if ($LASTEXITCODE) { throw "dotnet tool install wix завершился с кодом $LASTEXITCODE" }
    & "$tools\wix.exe" burn extract $exe.FullName -o "$work\bundle" | Out-Host
    if ($LASTEXITCODE) { throw "wix burn extract завершился с кодом $LASTEXITCODE" }

    # Пакеты бандла лежат без расширения (a0, a1, ...): MSI узнаём по сигнатуре составного файла OLE
    $ole = [byte[]](0xD0, 0xCF, 0x11, 0xE0)
    $msis = Get-ChildItem "$work\bundle" -File | Where-Object {
        $head = [byte[]]::new(4)
        $fs = [IO.File]::OpenRead($_.FullName)
        try { [void]$fs.Read($head, 0, 4) } finally { $fs.Dispose() }
        -not (Compare-Object $head $ole)
    }
    if (-not $msis) { throw 'В бандле не найден MSI клиента' }

    $admin = Join-Path $work 'admin'
    foreach ($msi in $msis) {
        $path = "$($msi.FullName).msi"
        Copy-Item $msi.FullName $path
        $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$path`"", '/qn', "TARGETDIR=`"$admin`"" -Wait -PassThru
        if ($p.ExitCode) { throw "msiexec /a $($msi.Name) завершился с кодом $($p.ExitCode)" }
    }

    # Нужны сборки из корня клиента: в extensions\ лежат копии других версий
    $client = Get-ChildItem $admin -Recurse -Filter 'Ascon.Pilot.Pilot.dll' | Select-Object -First 1
    if (-not $client) { throw 'В MSI нет Ascon.Pilot.Pilot.dll' }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($name in $assemblies) {
        $file = Join-Path $client.DirectoryName "$name.dll"
        if (-not (Test-Path $file)) { throw "В клиенте нет $name.dll" }
        Copy-Item $file $Destination -Force
        Write-Host ('  {0,-40} {1}' -f "$name.dll", (Get-Item $file).VersionInfo.FileVersion)
    }
    Set-Content -LiteralPath $stamp -Value $Version -Encoding ascii
    Write-Host "Сборки клиента Pilot-BIM $Version в $Destination"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
