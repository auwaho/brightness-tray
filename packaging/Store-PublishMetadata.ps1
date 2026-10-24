Set-StrictMode -Version Latest

function Assert-StoreFrameworkIdentity([System.Collections.IDictionary]$Identity) {
    if ($Identity.Name -ne 'Microsoft.WindowsAppRuntime.2' -or
        $Identity.Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
        $Identity.ProcessorArchitecture -ne 'x64' -or [version]$Identity.Version -lt [version]'2.5.1.0') {
        throw 'The resolved dependency is not a supported Microsoft x64 Windows App Runtime framework.'
    }
}

function Get-StorePublishFingerprint([string]$PublishDirectory) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'BrightnessTray.exe') -PathType Leaf)) {
        throw 'The Store publish output is missing. Run scripts/publish.ps1 -Store.'
    }
    $taskPublishRoot = [IO.Path]::GetFullPath($PublishDirectory)
    $taskRecords = @(Get-ChildItem -LiteralPath $taskPublishRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $taskRelative = [IO.Path]::GetRelativePath($taskPublishRoot, $_.FullName).Replace('\', '/')
        $taskHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$taskRelative`:$taskHash"
    })
    $taskBytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $taskRecords))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskBytes))
}

function Write-StorePublishMetadata([string]$PublishDirectory, [string]$AssetsPath, [string]$MetadataPath) {
    $taskPublishRoot = [IO.Path]::GetFullPath($PublishDirectory) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($MetadataPath).StartsWith($taskPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Store dependency metadata must be outside the publish folder.'
    }
    # Resolve the dependency once, immediately after this Store publish, while its assets are authoritative.
    $taskAssets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
    $taskRuntimeKey = @($taskAssets.libraries.Keys | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' })
    if ($taskRuntimeKey.Count -ne 1) { throw 'The Store publish did not resolve one Windows App SDK Runtime package.' }
    $taskRuntimeRelative = $taskAssets.libraries[$taskRuntimeKey[0]].path
    $taskRuntimePackage = @($taskAssets.packageFolders.Keys | ForEach-Object {
        Join-Path $_ "$taskRuntimeRelative/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.2.msix"
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    if ($taskRuntimePackage.Count -ne 1) { throw 'The resolved Windows App Runtime framework MSIX is missing.' }
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskRuntimePackage[0])
    try {
        $taskEntry = $taskZip.GetEntry('AppxManifest.xml')
        if ($null -eq $taskEntry) { throw 'The resolved Windows App Runtime framework has no manifest.' }
        $taskReader = [IO.StreamReader]::new($taskEntry.Open())
        try { [xml]$taskFramework = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
    } finally { $taskZip.Dispose() }
    if ($taskFramework.Package.Properties.Framework -ne 'true') { throw 'The resolved dependency is not a framework package.' }
    $taskIdentity = [ordered]@{}
    foreach ($taskAttribute in @('Name', 'Publisher', 'Version', 'ProcessorArchitecture')) {
        $taskIdentity[$taskAttribute] = [string]$taskFramework.Package.Identity.GetAttribute($taskAttribute)
    }
    Assert-StoreFrameworkIdentity $taskIdentity
    $taskMetadata = [ordered]@{
        SchemaVersion = 1
        RuntimePackage = $taskRuntimeKey[0]
        RuntimeMsixSha256 = (Get-FileHash -LiteralPath $taskRuntimePackage[0] -Algorithm SHA256).Hash
        Framework = $taskIdentity
        PublishSha256 = Get-StorePublishFingerprint $PublishDirectory
    }
    $taskMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $MetadataPath -Encoding utf8
}

function Read-StorePublishMetadata([string]$PublishDirectory, [string]$MetadataPath) {
    if (-not (Test-Path -LiteralPath $MetadataPath -PathType Leaf)) {
        throw 'The Store publish dependency metadata is missing. Run scripts/publish.ps1 -Store.'
    }
    $taskMetadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json -AsHashtable
    if ($taskMetadata.SchemaVersion -ne 1 -or
        $taskMetadata.RuntimePackage -notlike 'Microsoft.WindowsAppSDK.Runtime/*' -or
        $taskMetadata.RuntimeMsixSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Invalid Store publish dependency metadata. Run scripts/publish.ps1 -Store.'
    }
    Assert-StoreFrameworkIdentity $taskMetadata.Framework
    if ($taskMetadata.PublishSha256 -ne (Get-StorePublishFingerprint $PublishDirectory)) {
        throw 'The Store publish output no longer matches its dependency metadata. Run scripts/publish.ps1 -Store.'
    }
    return $taskMetadata.Framework
}
