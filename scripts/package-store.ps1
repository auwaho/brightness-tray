[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version = '0.1.4.0',
    [string]$PackageName,
    [string]$Publisher,
    [switch]$SkipPublish
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageName) -ne [string]::IsNullOrWhiteSpace($Publisher)) {
    throw 'Supply both PackageName and Publisher from Partner Center, or omit both for a development-identity preview.'
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

# Read the official framework identity from the exact Runtime package used by this build.
$taskAssetsPath = Join-Path $taskRoot 'src/BrightnessTray.App/obj/project.assets.json'
$taskAssets = Get-Content -LiteralPath $taskAssetsPath -Raw | ConvertFrom-Json -AsHashtable
$taskRuntimeKey = @($taskAssets.libraries.Keys | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' })
if ($taskRuntimeKey.Count -ne 1) { throw 'The Store publish did not resolve one Windows App SDK Runtime package.' }
$taskRuntimeRelative = $taskAssets.libraries[$taskRuntimeKey[0]].path
$taskRuntimePackage = @($taskAssets.packageFolders.Keys | ForEach-Object {
    Join-Path $_ "$taskRuntimeRelative/tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.2.msix"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
if ($taskRuntimePackage.Count -ne 1) { throw 'The resolved Windows App Runtime framework MSIX is missing.' }
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskRuntimePackage[0])
try {
    $taskReader = [IO.StreamReader]::new($taskZip.GetEntry('AppxManifest.xml').Open())
    try { [xml]$taskFramework = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
} finally { $taskZip.Dispose() }
if ($taskFramework.Package.Properties.Framework -ne 'true' -or $taskFramework.Package.Identity.ProcessorArchitecture -ne 'x64') {
    throw 'The dependency is not an x64 framework package.'
}

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
}
$taskDependency = $taskManifest.CreateElement('PackageDependency', $taskManifest.DocumentElement.NamespaceURI)
foreach ($taskAttribute in @('Name', 'Publisher')) {
    $taskDependency.SetAttribute($taskAttribute, [string]$taskFramework.Package.Identity.GetAttribute($taskAttribute))
}
$taskDependency.SetAttribute('MinVersion', [string]$taskFramework.Package.Identity.Version)
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
    Write-Host 'Development identity preview. Rebuild with PackageName and Publisher from Partner Center before submission.'
}
