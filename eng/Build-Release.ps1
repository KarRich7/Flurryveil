#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $DotNet = 'dotnet',
    [string] $InnoCompiler = $env:INNO_ISCC,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts'),
    [string] $ReleaseTag
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'Snowfall/Snowfall.csproj'
$installer = Join-Path $repoRoot 'installer/Flurryveil.iss'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version

if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw 'Set a MAJOR.MINOR.PATCH release version in Snowfall/Snowfall.csproj.'
}
if ($ReleaseTag -and $ReleaseTag -cne "v$version") {
    throw "Tag '$ReleaseTag' differs from the project release version v$version."
}

if (-not $InnoCompiler) {
    $InnoCompiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}
if (-not $InnoCompiler) {
    $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
}
if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6.7.4 or pass the full path to ISCC.exe with -InnoCompiler.'
}
$InnoCompiler = (Resolve-Path -LiteralPath $InnoCompiler).Path
$DotNet = (Get-Command $DotNet -CommandType Application | Select-Object -First 1).Source
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName

$stage = [IO.Directory]::CreateTempSubdirectory('Flurryveil-package-').FullName
$payload = Join-Path $stage 'payload'
$prefix = "Flurryveil-$version-win-x64"
Push-Location -LiteralPath $repoRoot
try {
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -o $payload `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Force)
    if ($payloadFiles.Count -ne 1 -or $payloadFiles[0].Name -cne 'Flurryveil.exe') {
        throw 'The self-contained publication must contain only Flurryveil.exe.'
    }

    $resolved = & $DotNet msbuild $project -p:RuntimeIdentifier=win-x64 -p:SelfContained=true `
        -t:ResolveFrameworkReferences -getItem:ResolvedRuntimePack
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the restored .NET runtime packs.' }
    $packs = ($resolved | ConvertFrom-Json).Items.ResolvedRuntimePack
    $corePack = ($packs | Where-Object FrameworkName -eq 'Microsoft.NETCore.App').PackageDirectory
    $desktopPack = ($packs | Where-Object FrameworkName -eq 'Microsoft.WindowsDesktop.App').PackageDirectory
    $licenses = (New-Item -ItemType Directory -Path (Join-Path $payload 'licenses')).FullName
    Copy-Item -LiteralPath (Join-Path $corePack 'LICENSE.TXT') (Join-Path $licenses 'dotnet-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $corePack 'THIRD-PARTY-NOTICES.TXT') (Join-Path $licenses 'dotnet-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $desktopPack 'LICENSE') (Join-Path $licenses 'windowsdesktop-LICENSE.txt')

    & $InnoCompiler '/Qp' "/O$stage" "/DAppVersion=$version" "/DPayloadDirectory=$payload" $installer
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

    $portable = Join-Path $stage "$prefix-Portable.zip"
    Compress-Archive -LiteralPath @($payloadFiles[0].FullName, $licenses) -DestinationPath $portable -CompressionLevel Optimal
    $packages = @((Join-Path $stage "$prefix-Setup.exe"), $portable)
    $checksum = Join-Path $stage "$prefix-SHA256SUMS.txt"
    $packages | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $_)
    } | Set-Content -LiteralPath $checksum -Encoding ascii
    foreach ($file in ($packages + $checksum)) {
        Move-Item -LiteralPath $file -Destination $output -Force
        Write-Output (Join-Path $output (Split-Path -Leaf $file))
    }
}
finally {
    Pop-Location
    $stage = [IO.Path]::GetFullPath($stage)
    $stageParent = [IO.Path]::GetDirectoryName($stage)
    if ((Split-Path -Leaf $stage) -like 'Flurryveil-package-*' -and
        $stageParent -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
