[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/windows",
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0-rc.1"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$OutputDirectory = [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory))
$Project = Join-Path $RepoRoot "src/DropShelf.App/DropShelf.App.csproj"
$PublishDirectory = Join-Path $OutputDirectory ".staging/publish"
$LayoutDirectory = Join-Path $OutputDirectory ".staging/msix-layout"
$UnpackedDirectory = Join-Path $OutputDirectory ".staging/msix-unpacked"
$ZipUnpackedDirectory = Join-Path $OutputDirectory ".staging/zip-unpacked"

if ($Version -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:[-+].*)?$') {
    throw "Version must be a semantic version with three numeric components."
}
$PackageVersion = "$($Matches.major).$($Matches.minor).$($Matches.patch).0"

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}
New-Item $PublishDirectory -ItemType Directory -Force | Out-Null
New-Item $LayoutDirectory -ItemType Directory -Force | Out-Null

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command"
    }
}

Push-Location $RepoRoot
try {
    Invoke-Native "dotnet" @("restore", $Project, "--runtime", "win-x64")
    Invoke-Native "dotnet" @(
        "publish", $Project,
        "--configuration", $Configuration,
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "-p:Version=$Version",
        "-p:PublishSingleFile=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:PublishDir=$PublishDirectory/"
    )

    $Status = @"
Drop Shelf Windows packaging status

- The ZIP and MSIX are unsigned development artifacts.
- They are not code-signed and are not claimed as production-distributable packages.
- The MSIX publisher is the development identity "CN=DropShelf Development".
- See docs/install-and-uninstall.md and docs/release-checklist.md.
"@
    [IO.File]::WriteAllText((Join-Path $PublishDirectory "PACKAGING-STATUS.txt"), $Status, [Text.UTF8Encoding]::new($false))

    Invoke-Native "python" @(
        (Join-Path $RepoRoot "scripts/ci/generate-license-inventory.py"),
        "--root", $RepoRoot,
        "--output", (Join-Path $OutputDirectory "DropShelf-windows-third-party-licenses.spdx.json")
    )

    $ZipPath = Join-Path $OutputDirectory "DropShelf-win-x64.zip"
    Invoke-Native "python" @(
        (Join-Path $RepoRoot "scripts/ci/create-deterministic-zip.py"),
        "--source", $PublishDirectory,
        "--output", $ZipPath,
        "--prefix", "DropShelf"
    )

    Copy-Item (Join-Path $PublishDirectory "*") $LayoutDirectory -Recurse -Force
    $Assets = Join-Path $LayoutDirectory "Assets"
    New-Item $Assets -ItemType Directory -Force | Out-Null
    $AssetSpecs = @(
        @("StoreLogo.png", 50, 50),
        @("Square44x44Logo.png", 44, 44),
        @("Square150x150Logo.png", 150, 150),
        @("Wide310x150Logo.png", 310, 150)
    )
    foreach ($Asset in $AssetSpecs) {
        Invoke-Native "python" @(
            (Join-Path $RepoRoot "scripts/ci/generate-package-assets.py"),
            "--output", (Join-Path $Assets $Asset[0]),
            "--width", [string]$Asset[1],
            "--height", [string]$Asset[2]
        )
    }

    $ManifestTemplate = [IO.File]::ReadAllText((Join-Path $RepoRoot "packaging/windows/AppxManifest.xml.in"))
    $Manifest = $ManifestTemplate.Replace("__PACKAGE_VERSION__", $PackageVersion)
    [IO.File]::WriteAllText((Join-Path $LayoutDirectory "AppxManifest.xml"), $Manifest, [Text.UTF8Encoding]::new($false))

    $WindowsKits = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($WindowsKits)) {
        throw "ProgramFiles(x86) is unavailable; the Windows SDK cannot be located."
    }
    $MakeAppx = Get-ChildItem (Join-Path $WindowsKits "Windows Kits/10/bin/*/x64/makeappx.exe") |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $MakeAppx) {
        throw "makeappx.exe was not found. Install the Windows 10/11 SDK."
    }

    $MsixPath = Join-Path $OutputDirectory "DropShelf-win-x64.msix"
    Invoke-Native $MakeAppx.FullName @("pack", "/o", "/d", $LayoutDirectory, "/p", $MsixPath)

    Expand-Archive $ZipPath -DestinationPath $ZipUnpackedDirectory -Force
    Invoke-Native (Join-Path $ZipUnpackedDirectory "DropShelf/DropShelf.App.exe") @("--package-smoke-test")

    Invoke-Native $MakeAppx.FullName @("unpack", "/o", "/p", $MsixPath, "/d", $UnpackedDirectory)
    Invoke-Native (Join-Path $UnpackedDirectory "DropShelf.App.exe") @("--package-smoke-test")

    Invoke-Native "python" @(
        (Join-Path $RepoRoot "scripts/ci/generate-checksums.py"),
        "--output", (Join-Path $OutputDirectory "DropShelf-windows-SHA256SUMS.txt"),
        $ZipPath,
        $MsixPath,
        (Join-Path $OutputDirectory "DropShelf-windows-third-party-licenses.spdx.json")
    )
}
finally {
    Pop-Location
    Remove-Item (Join-Path $OutputDirectory ".staging") -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Windows packages created and smoke-tested in $OutputDirectory"
