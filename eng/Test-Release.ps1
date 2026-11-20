#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '../artifacts'),
    [string] $PreviousInstaller,
    [switch] $Launch,
    [switch] $TestLegacyStartup
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../Snowfall/Snowfall.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$prefix = "Flurryveil-$version-win-x64"
$installer = (Resolve-Path -LiteralPath (Join-Path $PackageDirectory "$prefix-Setup.exe")).Path
$portable = (Resolve-Path -LiteralPath (Join-Path $PackageDirectory "$prefix-Portable.zip")).Path
$checksum = Join-Path $PackageDirectory "$prefix-SHA256SUMS.txt"
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Flurryveil.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Flurryveil.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{412DF28B-9607-49BA-827C-71BEC140207A}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$firstInstaller = if ($PreviousInstaller) { (Resolve-Path -LiteralPath $PreviousInstaller).Path } else { $installer }

if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $startMenuLink) -or (Test-Path -LiteralPath $desktopLink)) {
    throw 'Run this check in a Windows user profile without Flurryveil installed.'
}
if ($TestLegacyStartup -and (Get-ItemProperty -LiteralPath $runKey -Name QuietSnow -ErrorAction SilentlyContinue)) {
    throw 'The migration check needs a Windows user profile without an existing QuietSnow startup entry.'
}
$sums = Get-Content -LiteralPath $checksum
foreach ($file in @($installer, $portable)) {
    $expected = '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
    if ($expected -cnotin $sums) { throw "Checksum mismatch for $file." }
}

function Invoke-Installer([string] $Path, [string[]] $Arguments) {
    $log = Join-Path $stage 'setup.log'
    $Arguments += '/LOG="{0}"' -f $log
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 20 | Write-Host }
        throw "$(Split-Path -Leaf $Path) exited with code $($process.ExitCode)."
    }
}

function Test-Launch([string] $Path) {
    $process = Start-Process -FilePath $Path -ArgumentList '--background' -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForInputIdle(15000)) { throw 'The application did not start its Windows message loop.' }
        if ($process.WaitForExit(1500)) { throw "The application exited with code $($process.ExitCode)." }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

$stage = [IO.Directory]::CreateTempSubdirectory('Flurryveil-test-').FullName
$target = Join-Path $stage 'Install with spaces'
$installedApp = Join-Path $target 'Flurryveil.exe'
$uninstaller = Join-Path $target 'unins000.exe'
$fixture = '"{0}" --background' -f (Join-Path $stage 'Тихий снег.exe')
$installedCommand = '"{0}" --background' -f $installedApp
$originalDataDirectory = $env:FLURRYVEIL_DATA_DIR
$originalExtractDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$installerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
try {
    $env:FLURRYVEIL_DATA_DIR = (New-Item -ItemType Directory -Path (Join-Path $stage 'User data')).FullName
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $stage 'Runtime cache'
    $settings = Join-Path $env:FLURRYVEIL_DATA_DIR 'settings.json'
    Set-Content -LiteralPath $settings -Value '{"Paused":true,"WeatherOpacity":65}'
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $portableDirectory = Join-Path $stage 'Portable'
    Expand-Archive -LiteralPath $portable -DestinationPath $portableDirectory
    $portableApp = Join-Path $portableDirectory 'Flurryveil.exe'
    if ($Launch) { Test-Launch $portableApp }

    if ($TestLegacyStartup) {
        if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $runKey -Name QuietSnow -Value $fixture -Type String
    }
    Invoke-Installer $firstInstaller ($installerArgs + @('/TASKS=desktopicon', ('/DIR="{0}"' -f $target)))
    $initialVersion = Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayVersion
    if ($PreviousInstaller -and ([version]$initialVersion -ge [version]$version)) {
        throw "The previous installer should install a version older than $version."
    }
    if ($TestLegacyStartup -and (Get-ItemPropertyValue -LiteralPath $runKey -Name QuietSnow) -cne $installedCommand) {
        throw 'The legacy startup entry was not redirected to the installed executable.'
    }

    $preservedFile = Join-Path $target 'user-content.txt'
    Set-Content -LiteralPath $preservedFile -Value 'user data'
    Invoke-Installer $installer $installerArgs
    if ((Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayVersion) -cne $version) {
        throw "The installed version should be $version."
    }
    if ((Get-FileHash -LiteralPath $installedApp).Hash -ne (Get-FileHash -LiteralPath $portableApp).Hash) {
        throw 'The installed executable does not match the portable package.'
    }
    foreach ($notice in @('dotnet-LICENSE.txt', 'dotnet-THIRD-PARTY-NOTICES.txt', 'windowsdesktop-LICENSE.txt')) {
        $portableNotice = Join-Path $portableDirectory "licenses/$notice"
        $installedNotice = Join-Path $target "licenses/$notice"
        if ((Get-FileHash -LiteralPath $portableNotice).Hash -ne (Get-FileHash -LiteralPath $installedNotice).Hash) {
            throw "The two packages contain different copies of $notice."
        }
    }
    if ((Get-Content -LiteralPath $preservedFile -Raw).Trim() -ne 'user data') {
        throw 'Reinstallation altered an unmanaged file.'
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @($startMenuLink, $desktopLink)) {
        if (-not (Test-Path -LiteralPath $link -PathType Leaf)) { throw "Missing shortcut: $link" }
        if ($shell.CreateShortcut($link).TargetPath -ine $installedApp) { throw "Incorrect shortcut target: $link" }
    }
    if ($Launch) { Test-Launch $installedApp }

    Invoke-Installer $uninstaller $installerArgs
    foreach ($path in @($installedApp, $startMenuLink, $desktopLink, $uninstallKey)) {
        if (Test-Path -LiteralPath $path) { throw "The uninstaller did not remove $path." }
    }
    if (-not (Test-Path -LiteralPath $preservedFile -PathType Leaf)) { throw 'Uninstallation removed an unmanaged file.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) {
        throw 'The settings file changed during the package smoke check.'
    }
    if ($TestLegacyStartup -and (Get-ItemProperty -LiteralPath $runKey -Name QuietSnow -ErrorAction SilentlyContinue)) {
        throw 'The uninstaller did not remove the startup entry for the installed executable.'
    }
    Write-Output "PASS: package checksums, install, update ($initialVersion → $version), shortcuts, settings preservation, and uninstall"
    if ($Launch) { Write-Output 'PASS: installed and portable applications launched' }
    if ($TestLegacyStartup) { Write-Output 'PASS: migration and removal of the legacy startup entry' }
}
finally {
    $env:FLURRYVEIL_DATA_DIR = $originalDataDirectory
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $originalExtractDirectory
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) { Invoke-Installer $uninstaller $installerArgs }
    if ($TestLegacyStartup) {
        $current = (Get-ItemProperty -LiteralPath $runKey -Name QuietSnow -ErrorAction SilentlyContinue).QuietSnow
        if ($current -cin @($fixture, $installedCommand)) { Remove-ItemProperty -LiteralPath $runKey -Name QuietSnow }
    }
    $stage = [IO.Path]::GetFullPath($stage)
    if ((Split-Path -Leaf $stage) -like 'Flurryveil-test-*' -and
        [IO.Path]::GetDirectoryName($stage) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
