Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $taskRoot 'packaging/Store-PublishMetadata.ps1')
$taskDirectory = Join-Path $taskRoot ('artifacts/packaging-metadata-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null

function New-StoreMetadataFixture([string]$Publisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US',
    [string]$Version = '2.5.1.0', [string]$Architecture = 'x64', [string]$Framework = 'true') {
    $taskCasePath = Join-Path $taskDirectory ([guid]::NewGuid().ToString('N'))
    $taskPublish = Join-Path $taskCasePath 'publish'
    New-Item -ItemType Directory -Path $taskPublish -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $taskPublish 'BrightnessTray.exe') -Value 'apphost fixture'
    Set-Content -LiteralPath (Join-Path $taskPublish 'BrightnessTray.dll') -Value 'Store app fixture'
    Set-Content -LiteralPath (Join-Path $taskPublish 'BrightnessTray.deps.json') -Value '{"Runtime":"2.5.1"}'
    $taskPackages = Join-Path $taskCasePath 'packages'
    $taskRuntimeRelative = 'microsoft.windowsappsdk.runtime/2.5.1'
    $taskRuntime = Join-Path $taskPackages "$taskRuntimeRelative/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.2.msix"
    New-Item -ItemType Directory -Path (Split-Path $taskRuntime -Parent) -Force | Out-Null
    $taskManifest = [xml]'<Package><Identity Name="Microsoft.WindowsAppRuntime.2" /><Properties /></Package>'
    $taskManifest.Package.Identity.SetAttribute('Publisher', $Publisher)
    $taskManifest.Package.Identity.SetAttribute('Version', $Version)
    $taskManifest.Package.Identity.SetAttribute('ProcessorArchitecture', $Architecture)
    $taskNode = $taskManifest.CreateElement('Framework')
    $taskNode.InnerText = $Framework
    $taskManifest.SelectSingleNode('/Package/Properties').AppendChild($taskNode) | Out-Null
    $taskZip = [IO.Compression.ZipFile]::Open($taskRuntime, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $taskWriter = [IO.StreamWriter]::new($taskZip.CreateEntry('AppxManifest.xml').Open())
        try { $taskWriter.Write($taskManifest.OuterXml) } finally { $taskWriter.Dispose() }
    } finally { $taskZip.Dispose() }
    $taskAssets = Join-Path $taskCasePath 'project.assets.json'
    @{
        libraries = @{ 'Microsoft.WindowsAppSDK.Runtime/2.5.1' = @{ path = $taskRuntimeRelative } }
        packageFolders = @{ $taskPackages = @{} }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskAssets
    return @{ PublishDirectory = $taskPublish; AssetsPath = $taskAssets; MetadataPath = $taskPublish + '.framework.json'; RuntimePath = $taskRuntime }
}

function Write-StoreFixtureMetadata([System.Collections.IDictionary]$Fixture) {
    Write-StorePublishMetadata -PublishDirectory $Fixture.PublishDirectory -AssetsPath $Fixture.AssetsPath -MetadataPath $Fixture.MetadataPath
}

function Read-StoreFixtureMetadata([System.Collections.IDictionary]$Fixture) {
    Read-StorePublishMetadata -PublishDirectory $Fixture.PublishDirectory -MetadataPath $Fixture.MetadataPath
}

function Assert-StoreMetadataFailure([scriptblock]$Action, [string]$Expected) {
    $taskError = $null
    try { & $Action | Out-Null } catch { $taskError = $_.Exception.Message }
    if ($null -eq $taskError -or -not $taskError.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Expected '$Expected', got '$taskError'."
    }
}

$taskCases = [ordered]@{
    'PublishedIdentitySurvivesNormalRestore' = {
        $taskFixture = New-StoreMetadataFixture
        Write-StoreFixtureMetadata $taskFixture
        $taskSaved = Get-Content -LiteralPath $taskFixture.MetadataPath -Raw | ConvertFrom-Json -AsHashtable
        if ($taskSaved.RuntimeMsixSha256 -ne (Get-FileHash -LiteralPath $taskFixture.RuntimePath -Algorithm SHA256).Hash) {
            throw 'Metadata lost its resolved framework provenance.'
        }
        # A normal restore removes Runtime from shared obj; even cleaning obj/cache must not invalidate the publish.
        Set-Content -LiteralPath $taskFixture.AssetsPath -Value '{"libraries":{},"packageFolders":{}}'
        Remove-Item -LiteralPath $taskFixture.RuntimePath
        $taskIdentity = Read-StoreFixtureMetadata $taskFixture
        if ($taskIdentity.Name -ne 'Microsoft.WindowsAppRuntime.2' -or $taskIdentity.Version -ne '2.5.1.0') {
            throw 'The existing publish did not retain its exact framework identity.'
        }
        if (Get-ChildItem -LiteralPath $taskFixture.PublishDirectory -Filter '*.framework.json') {
            throw 'The dependency sidecar would be copied into the MSIX.'
        }
    }
    'ChangedOutputRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Write-StoreFixtureMetadata $taskFixture
        Set-Content -LiteralPath (Join-Path $taskFixture.PublishDirectory 'BrightnessTray.dll') -Value 'different app'
        Assert-StoreMetadataFailure { Read-StoreFixtureMetadata $taskFixture } 'no longer matches'
    }
    'AddedOutputRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Write-StoreFixtureMetadata $taskFixture
        Set-Content -LiteralPath (Join-Path $taskFixture.PublishDirectory 'Microsoft.UI.Xaml.dll') -Value 'wrong private runtime'
        Assert-StoreMetadataFailure { Read-StoreFixtureMetadata $taskFixture } 'no longer matches'
    }
    'RemovedOutputRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Write-StoreFixtureMetadata $taskFixture
        Remove-Item -LiteralPath (Join-Path $taskFixture.PublishDirectory 'BrightnessTray.deps.json')
        Assert-StoreMetadataFailure { Read-StoreFixtureMetadata $taskFixture } 'no longer matches'
    }
    'MissingMetadataRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Assert-StoreMetadataFailure { Read-StoreFixtureMetadata $taskFixture } 'metadata is missing'
    }
    'InvalidIdentityRejectedAtPublish' = {
        foreach ($taskFixture in @(
            (New-StoreMetadataFixture -Publisher 'CN=Other'),
            (New-StoreMetadataFixture -Version '2.0.0.0'),
            (New-StoreMetadataFixture -Architecture 'arm64')
        )) {
            Assert-StoreMetadataFailure { Write-StoreFixtureMetadata $taskFixture } 'supported Microsoft x64'
        }
        $taskFixture = New-StoreMetadataFixture -Framework 'false'
        Assert-StoreMetadataFailure { Write-StoreFixtureMetadata $taskFixture } 'not a framework'
    }
    'MissingResolvedRuntimeRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Remove-Item -LiteralPath $taskFixture.RuntimePath
        Assert-StoreMetadataFailure { Write-StoreFixtureMetadata $taskFixture } 'framework MSIX is missing'
    }
    'InvalidSavedIdentityRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Write-StoreFixtureMetadata $taskFixture
        $taskSaved = Get-Content -LiteralPath $taskFixture.MetadataPath -Raw | ConvertFrom-Json -AsHashtable
        $taskSaved.Framework.Publisher = 'CN=Other'
        $taskSaved | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $taskFixture.MetadataPath
        Assert-StoreMetadataFailure { Read-StoreFixtureMetadata $taskFixture } 'supported Microsoft x64'
    }
    'MetadataInsideOutputRejected' = {
        $taskFixture = New-StoreMetadataFixture
        Assert-StoreMetadataFailure {
            Write-StorePublishMetadata -PublishDirectory $taskFixture.PublishDirectory -AssetsPath $taskFixture.AssetsPath `
                -MetadataPath (Join-Path $taskFixture.PublishDirectory 'framework.json')
        } 'outside the publish folder'
    }
}

foreach ($taskCase in $taskCases.GetEnumerator()) {
    & $taskCase.Value
    Write-Output "PASS Store publish metadata: $($taskCase.Key)"
}
Write-Output "$($taskCases.Count)/$($taskCases.Count) Store publish metadata checks passed."
