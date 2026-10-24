[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    dotnet build BrightnessTray.slnx -c Release --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/BrightnessTray.Tests/BrightnessTray.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & (Join-Path $taskRoot 'tests/BrightnessTray.Packaging.Tests/Test-Package.Tests.ps1')
} finally { Pop-Location }
