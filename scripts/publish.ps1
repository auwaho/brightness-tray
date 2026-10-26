[CmdletBinding()]
param([switch]$Store)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1')
Push-Location $taskRoot
try {
    $taskOutput = Join-Path $taskRoot $(if ($Store) { 'artifacts/publish/store/win-x64' } else { 'artifacts/publish/win-x64' })
    # Publishing over an old folder would retain binaries from removed package dependencies.
    $taskOutput = [IO.Path]::GetFullPath($taskOutput)
    $taskAllowed = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/publish')) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskOutput.StartsWith($taskAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish path.' }
    $taskMetadataPath = $taskOutput + '.framework.json'
    if ($Store -and (Test-Path -LiteralPath $taskMetadataPath)) { Remove-Item -LiteralPath $taskMetadataPath -Force }
    if (Test-Path -LiteralPath $taskOutput) { Remove-Item -LiteralPath $taskOutput -Recurse -Force }
    $taskPublishArguments = @('publish', 'src/BrightnessTray.App/BrightnessTray.App.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--nologo', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false',
        '-p:PublishTrimmed=false', '-o', $taskOutput)
    if ($Store) { $taskPublishArguments += '-p:BrightnessTrayStoreBuild=true' }
    dotnet @taskPublishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    & (Join-Path $taskRoot 'packaging/Copy-ThirdPartyNotices.ps1') -PublishDirectory $taskOutput `
        -AssetsPath (Join-Path $taskRoot 'src/BrightnessTray.App/obj/project.assets.json')
    if ($Store) {
        . (Join-Path $taskRoot 'packaging/Store-PublishMetadata.ps1')
        Write-StorePublishMetadata -PublishDirectory $taskOutput -MetadataPath $taskMetadataPath `
            -AssetsPath (Join-Path $taskRoot 'src/BrightnessTray.App/obj/project.assets.json')
    }
    Write-Host "Ready: $(Join-Path $taskOutput 'BrightnessTray.exe')"
} finally { Pop-Location }
