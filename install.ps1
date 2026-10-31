<#
.SYNOPSIS
    Builds PeripheralHub and installs a development copy for the current user.

.DESCRIPTION
    For development only: users install with PeripheralHub-win-Setup.exe (build-installer.ps1).
    - Publishes a single PeripheralHub.exe (Release, framework-dependent: needs the .NET 8 Desktop Runtime) plus its
      built-in device plugins (a plugins\ folder next to the exe; it is replaced on each install).
    - Stops a running copy, then installs to %LOCALAPPDATA%\Programs\PeripheralHub (no admin rights needed).
    - Adds a Start-menu shortcut.
    - If "Start with Windows" is on, points it at the installed exe.
    - Starts the installed app.
    Run it again after changing the code to update the installed copy.

.PARAMETER Uninstall
    Removes the installed app, its Start-menu shortcut and its "Start with Windows" entry.
    Settings in %APPDATA%\PeripheralHub are kept.
#>
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\PeripheralHub'
$exe        = Join-Path $installDir 'PeripheralHub.exe'
$shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'PeripheralHub.lnk'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-App {
    Get-Process -Name PeripheralHub -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping running PeripheralHub (pid $($_.Id))..."
        $_ | Stop-Process -Force
        $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
    Stop-App
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Remove-ItemProperty -Path $runKey -Name PeripheralHub -ErrorAction SilentlyContinue
    Write-Host 'PeripheralHub uninstalled (settings in %APPDATA%\PeripheralHub were kept).'
    return
}

$repo = $PSScriptRoot
$publishDir = Join-Path $repo 'artifacts\publish'

Write-Host 'Publishing...'
dotnet publish (Join-Path $repo 'src\PeripheralHub.App\PeripheralHub.App.csproj') -c Release -o $publishDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Stop-App
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $publishDir 'PeripheralHub.exe') $exe -Force
$pluginsDir = Join-Path $installDir 'plugins'
if (Test-Path $pluginsDir) { Remove-Item $pluginsDir -Recurse -Force }
Copy-Item (Join-Path $publishDir 'plugins') $pluginsDir -Recurse
$pluginCount = (Get-ChildItem $pluginsDir -Directory).Count
if ($pluginCount -eq 0) { throw 'No plugins were published (expected plugins\<id> folders).' }
Write-Host "Installed to $exe (with $pluginCount built-in plugins)"

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'Battery and connection status for your peripherals'
$lnk.Save()
Write-Host "Start-menu shortcut: $shortcut"

if (Get-ItemProperty -Path $runKey -Name PeripheralHub -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $runKey -Name PeripheralHub -Value "`"$exe`""
    Write-Host 'Start with Windows now points at the installed copy.'
}

Start-Process $exe
Write-Host 'PeripheralHub started.'
