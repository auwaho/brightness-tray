[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version = '0.1.4.0',
    [string]$PackageName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [ValidateNotNullOrEmpty()][string]$DisplayName = 'Brightness Tray',
    [switch]$SkipPublish
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskIdentityValues = @($PackageName, $Publisher, $PublisherDisplayName)
$taskIdentityCount = @($taskIdentityValues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
if ($taskIdentityCount -ne 0 -and $taskIdentityCount -ne 3) {
    throw 'Supply PackageName, Publisher and PublisherDisplayName from Partner Center together, or omit all three for a development-identity preview.'
}
if ($taskIdentityCount -eq 3 -and $Publisher -eq 'CN=BrightnessTray Development') {
    throw 'A Store product identity must not use the development publisher.'
}
if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    throw 'DisplayName must match the reserved app name and must not be blank.'
}
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') -Store }
$taskOutput = Join-Path $taskRoot 'artifacts/msix/store'
$taskPublished = Join-Path $taskRoot 'artifacts/publish/store/win-x64'
$taskSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$taskSdk = Get-ChildItem -LiteralPath $taskSdkRoot -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path (Join-Path $_.FullName 'x64/makeappx.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $taskSdk) { throw 'Windows SDK with MakeAppx is required.' }

# The sidecar belongs to this published output; ordinary restores may replace the shared obj assets.
. (Join-Path $taskRoot 'packaging/Store-PublishMetadata.ps1')
$taskFramework = Read-StorePublishMetadata -PublishDirectory $taskPublished -MetadataPath ($taskPublished + '.framework.json')

New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskStage = [IO.Path]::GetFullPath((Join-Path $taskOutput 'staging'))
$taskAllowed = [IO.Path]::GetFullPath($taskOutput) + [IO.Path]::DirectorySeparatorChar
if (-not $taskStage.StartsWith($taskAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe Store staging path.' }
if (Test-Path -LiteralPath $taskStage) { Remove-Item -LiteralPath $taskStage -Recurse -Force }
New-Item -ItemType Directory -Path $taskStage | Out-Null
Get-ChildItem -LiteralPath $taskPublished | Copy-Item -Destination $taskStage -Recurse
& (Join-Path $taskRoot 'packaging/Create-Assets.ps1') -OutputDirectory (Join-Path $taskStage 'Assets')
[xml]$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'packaging/AppxManifest.xml') -Raw
$taskManifest.Package.Identity.Version = $Version
if (-not [string]::IsNullOrWhiteSpace($PackageName)) {
    $taskManifest.Package.Identity.Name = $PackageName
    $taskManifest.Package.Identity.Publisher = $Publisher
    $taskManifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
}
$taskManifest.Package.Properties.DisplayName = $DisplayName
$taskManifest.Package.Applications.Application.VisualElements.DisplayName = $DisplayName
$taskManifest.Package.Applications.Application.Extensions.Extension.StartupTask.DisplayName = $DisplayName
$taskDependency = $taskManifest.CreateElement('PackageDependency', $taskManifest.DocumentElement.NamespaceURI)
foreach ($taskAttribute in @('Name', 'Publisher')) {
    $taskDependency.SetAttribute($taskAttribute, [string]$taskFramework[$taskAttribute])
}
$taskDependency.SetAttribute('MinVersion', [string]$taskFramework.Version)
$taskManifest.Package.Dependencies.AppendChild($taskDependency) | Out-Null
$taskManifest.Save((Join-Path $taskStage 'AppxManifest.xml'))
$taskPackage = Join-Path $taskOutput "BrightnessTray_${Version}_x64_Store.msix"
$taskLog = Join-Path $taskOutput 'makeappx.log'
& (Join-Path $taskSdk.FullName 'x64/makeappx.exe') pack /d $taskStage /p $taskPackage /o *> $taskLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $taskLog -Tail 20 | Write-Host
    throw 'Store MSIX validation or packaging failed.'
}
& (Join-Path $taskRoot 'packaging/Test-Package.ps1') -PackagePath $taskPackage -Store |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'validation.json')
Write-Host "Unsigned Store package: $taskPackage"
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    Write-Host 'Development identity preview. Rebuild with PackageName, Publisher and PublisherDisplayName from Partner Center before submission.'
} else {
    Write-Host 'Product identity configured. Installation, WACK and Store certification remain separate release checks.'
}
