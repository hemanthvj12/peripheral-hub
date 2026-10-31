<#
.SYNOPSIS
    Builds the PeripheralHub installer (PeripheralHub-win-Setup.exe) and its update packages.

.DESCRIPTION
    - Publishes the app (single exe + plugins folder) with the given version.
    - Packs it with Velopack (vpk, pinned in .config/dotnet-tools.json) into artifacts\installer:
        PeripheralHub-win-Setup.exe   the installer to give to people (installs per user, no admin rights;
                                      installs the .NET 8 Desktop Runtime if it's missing)
        *.nupkg, releases.win.json    update packages (the app checks GitHub Releases for these)
    - With -Publish, uploads everything as a GitHub release, which installed copies then update to.
    Users install to %LOCALAPPDATA%\PeripheralHub, get a Start-menu shortcut and an entry in Settings > Apps,
    where Uninstall removes it (see Installation.cs for the clean-up it runs).

.PARAMETER Version
    The release version, e.g. 1.1.0. Must be higher than the last published release for updates to pick it up.

.PARAMETER Publish
    Upload to https://github.com/hemanthvj12/peripheral-hub releases. Uses $env:GITHUB_TOKEN when set (the release
    workflow, .github/workflows/release.yml), else the gh CLI's login.
    Installed copies pick the new release up at their next update check.
    Normally you don't run this with -Publish yourself: push a tag (git tag v1.2.3; git push origin v1.2.3) and
    GitHub builds, tests and publishes the release.

.EXAMPLE
    ./build-installer.ps1 -Version 1.1.0
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$repoUrl = 'https://github.com/hemanthvj12/peripheral-hub'
$publishDir = Join-Path $repo 'artifacts\installer-publish'
$outputDir = Join-Path $repo 'artifacts\installer'

Push-Location $repo
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

    # Start clean: leftovers from local test builds (e.g. a higher test version) would clash with this release.
    # With -Publish the previous published release is downloaded again below, for delta updates.
    foreach ($dir in $publishDir, $outputDir) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    Write-Host "Publishing version $Version..."
    dotnet publish 'src\PeripheralHub.App\PeripheralHub.App.csproj' -c Release -o $publishDir -p:Version=$Version --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    # Only the exe and the plugins ship; symbols and docs from referenced projects don't.
    Get-ChildItem $publishDir -File | Where-Object Name -ne 'PeripheralHub.exe' | Remove-Item -Force
    if (-not (Test-Path (Join-Path $publishDir 'plugins'))) { throw 'No plugins folder was published.' }

    if ($Publish) {
        # Fetch the previous release so vpk can build a small delta update from it.
        $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { (& 'C:\Program Files\GitHub CLI\gh.exe' auth token).Trim() }
        if (-not $token) { throw 'No GitHub token: set GITHUB_TOKEN or log in with gh.' }
        dotnet vpk download github --repoUrl $repoUrl --token $token -o $outputDir
    }

    Write-Host 'Packing installer...'
    dotnet vpk pack `
        --packId PeripheralHub `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe PeripheralHub.exe `
        --packTitle PeripheralHub `
        --packAuthors 'PeripheralHub' `
        --icon 'src\PeripheralHub.App\Assets\app.ico' `
        --runtime win-x64 `
        --framework net8-x64-desktop `
        --shortcuts StartMenuRoot `
        --noPortable `
        --outputDir $outputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

    if ($Publish) {
        dotnet vpk upload github --repoUrl $repoUrl --token $token -o $outputDir --publish `
            --releaseName "PeripheralHub $Version" --tag "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
        Write-Host "Published release v$Version."
    }

    Write-Host "Installer: $(Join-Path $outputDir 'PeripheralHub-win-Setup.exe')"
}
finally { Pop-Location }
