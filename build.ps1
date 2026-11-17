#requires -Version 3.0
<#
    build.ps1 - build SW2Visual.dll

    The SolidWorks interop assemblies are Dassault redistributables and are not part of
    this repository. This script locates them in the local SolidWorks installation,
    temporarily points the project at them, builds, and restores the project file.

    Usage:
        build.cmd
        build.cmd "D:\path\to\api\redist"
#>
param([string]$ApiDir)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root 'src\SW2URDF\SW2URDF.csproj'

if (-not (Test-Path -LiteralPath $csproj)) {
    Write-Host "[ERROR] Project not found: $csproj"
    exit 1
}

# ---- locate the SolidWorks API interop assemblies --------------------------
if (-not $ApiDir) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'SOLIDWORKS Corp\SOLIDWORKS\api\redist'),
        'D:\SOLIDWORKS\SOLIDWORKS Corp\SOLIDWORKS\api\redist',
        'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist',
        (Join-Path ${env:ProgramFiles(x86)} 'SOLIDWORKS Corp\SOLIDWORKS\api\redist')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath (Join-Path $c 'SolidWorks.Interop.sldworks.dll'))) {
            $ApiDir = $c
            break
        }
    }
}
if (-not $ApiDir -or -not (Test-Path -LiteralPath (Join-Path $ApiDir 'SolidWorks.Interop.sldworks.dll'))) {
    Write-Host "[ERROR] SolidWorks API interop assemblies not found."
    Write-Host '        Pass the folder containing SolidWorks.Interop.sldworks.dll:'
    Write-Host '            build.cmd "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"'
    exit 1
}
Write-Host "[INFO]  SolidWorks API: $ApiDir"

# ---- locate MSBuild --------------------------------------------------------
$msbuildCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2017\BuildTools\MSBuild\15.0\Bin\MSBuild.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'MSBuild\14.0\Bin\MSBuild.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe')
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $msbuild) {
    Write-Host '[ERROR] MSBuild.exe not found.'
    exit 1
}
Write-Host "[INFO]  MSBuild: $msbuild"

# ---- ensure the NuGet packages -------------------------------------------------
# packages/ is git-ignored, so a fresh clone has neither CsvHelper nor MathNet.
$packagesDir = Join-Path $root 'src\packages'
$need = @(
    (Join-Path $packagesDir 'CsvHelper.7.1.1\lib\net45\CsvHelper.dll'),
    (Join-Path $packagesDir 'MathNet.Numerics.Signed.4.7.0\lib\net40\MathNet.Numerics.dll')
)
if ($need | Where-Object { -not (Test-Path -LiteralPath $_) }) {
    $tools = Join-Path $root '.tools'
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    $nuget = Join-Path $tools 'nuget.exe'
    if (-not (Test-Path -LiteralPath $nuget)) {
        Write-Host '[INFO]  Downloading nuget.exe ...'
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' `
                -OutFile $nuget -UseBasicParsing
        }
        catch {
            Write-Host "[ERROR] Could not download nuget.exe: $($_.Exception.Message)"
            Write-Host '        Restore the packages manually, or build from Visual Studio once.'
            exit 1
        }
    }
    # Keep every NuGet artefact inside the repo: it avoids depending on the user-wide
    # cache and works on machines where %APPDATA% is read-only.
    $nugetConfig = Join-Path $tools 'NuGet.config'
    if (-not (Test-Path -LiteralPath $nugetConfig)) {
        @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding UTF8
    }
    $env:NUGET_SCRATCH  = Join-Path $tools 'scratch'
    $env:NUGET_PACKAGES = Join-Path $tools 'nuget-cache'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $tools 'nuget-http'

    Write-Host '[INFO]  Restoring NuGet packages ...'
    & $nuget restore (Join-Path $root 'src\SW2URDF\packages.config') `
        -PackagesDirectory $packagesDir -NonInteractive -ConfigFile $nugetConfig
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] nuget restore failed with code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host '[INFO]  Packages restored.'
}
# ---- patch, build, restore -------------------------------------------------
$backup = $csproj + '.bak'
Copy-Item -LiteralPath $csproj -Destination $backup -Force
$rc = 1
try {
    $t = [IO.File]::ReadAllText($csproj)
    foreach ($d in @('SolidWorks.Interop.sldworks',
                     'SolidWorks.Interop.swconst',
                     'SolidWorks.Interop.swpublished')) {
        $full = Join-Path $ApiDir ($d + '.dll')
        $t = [regex]::Replace($t, '(?<=<HintPath>)[^<]*' + [regex]::Escape($d) + '\.dll', $full)
    }
    [IO.File]::WriteAllText($csproj, $t)

    & $msbuild $csproj /p:Configuration=Debug /p:Platform=x64 `
        /p:RegisterForComInterop=false "/p:PostBuildEvent=" /v:minimal /nologo
    $rc = $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $backup) {
        Move-Item -LiteralPath $backup -Destination $csproj -Force
    }
}

if ($rc -ne 0) {
    Write-Host "[ERROR] Build failed with code $rc"
    exit $rc
}

Write-Host ''
Write-Host "[ OK ]  $(Join-Path $root 'src\SW2URDF\bin\x64\Debug\SW2Visual.dll')"
exit 0
