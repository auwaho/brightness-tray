[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/smoke-ui',
    [switch]$NativeCapture,
    [switch]$SharedRuntime
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.packages'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $taskProject = 'tests/BrightnessTray.App.SmokeTests/BrightnessTray.App.SmokeTests.csproj'
    $taskBuild = Join-Path $taskRoot 'tests/BrightnessTray.App.SmokeTests/bin/Release/net10.0-windows10.0.26100.0/win-x64'
    if ($SharedRuntime) {
        # A fresh folder prevents private WinUI DLLs from masking the shared-framework dependency.
        $taskPublish = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/ui-test/shared-runtime'))
        $taskAllowed = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/ui-test')) + [IO.Path]::DirectorySeparatorChar
        if (-not $taskPublish.StartsWith($taskAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe UI test publish path.' }
        if (Test-Path -LiteralPath $taskPublish) { Remove-Item -LiteralPath $taskPublish -Recurse -Force }
        dotnet publish $taskProject -c Release -r win-x64 --self-contained true --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:BrightnessTrayStoreBuild=true -p:PublishTrimmed=false -o $taskPublish
        if ($LASTEXITCODE -ne 0) { throw 'Shared-runtime UI test publish failed.' }
        foreach ($taskResource in @('App.xbf', 'FlyoutWindow.xbf', 'BrightnessTray.App.SmokeTests.pri')) {
            Copy-Item -LiteralPath (Join-Path $taskBuild $taskResource) -Destination $taskPublish -Force
        }
        $taskExe = Join-Path $taskPublish 'BrightnessTray.App.SmokeTests.exe'
    } else {
        dotnet build $taskProject -c Release --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
        $taskExe = Join-Path $taskBuild 'BrightnessTray.App.SmokeTests.exe'
    }
    $taskOutput = [IO.Path]::GetFullPath($OutputDirectory, $taskRoot)
    $taskArguments = '"' + $taskOutput + '"'
    if ($NativeCapture) { $taskArguments += ' --native-capture' }
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList $taskArguments -PassThru -WindowStyle Hidden
    if (-not $taskProcess.WaitForExit(30000)) {
        $taskProcess.Kill()
        throw 'UI test timed out.'
    }
    if ($taskProcess.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $taskOutput 'failure.txt')
        throw "UI test failed (exit code $($taskProcess.ExitCode))."
    }
    Write-Host "UI test passed: $taskOutput"
} finally { Pop-Location }
