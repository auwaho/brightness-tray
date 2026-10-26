[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$AssetsPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskAssets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$taskRuntime = Get-Content -LiteralPath (Join-Path $PublishDirectory 'BrightnessTray.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
$taskDeps = Get-Content -LiteralPath (Join-Path $PublishDirectory 'BrightnessTray.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$taskPackages = [ordered]@{}
foreach ($taskLibrary in $taskAssets.libraries.GetEnumerator()) {
    if ($taskLibrary.Value.type -eq 'package') { $taskPackages[$taskLibrary.Key] = $taskLibrary.Value.path }
}
foreach ($taskFramework in $taskRuntime.runtimeOptions.includedFrameworks) {
    $taskId = $taskFramework.name + '.Runtime.win-x64'
    $taskPackages[$taskId + '/' + $taskFramework.version] = $taskId.ToLowerInvariant() + '/' + $taskFramework.version
}
function Find-NoticePackage([string]$RelativePath) {
    foreach ($taskFolder in $taskAssets.packageFolders.Keys) {
        $taskPath = Join-Path $taskFolder $RelativePath
        if (Test-Path -LiteralPath $taskPath -PathType Container) { return $taskPath }
    }
    throw "Resolved package is unavailable for notice collection: $RelativePath"
}
$taskRequired = @{
    'Microsoft.NETCore.App.Runtime.win-x64' = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
    'Microsoft.WindowsDesktop.App.Runtime.win-x64' = @('LICENSE')
    'Microsoft.WindowsAppSDK.WinUI' = @('license.txt', 'NOTICE.txt')
    'Microsoft.WindowsAppSDK.Base' = @('license.txt', 'NOTICE.txt')
    'Microsoft.WindowsAppSDK.Foundation' = @('license.txt')
    'Microsoft.WindowsAppSDK.InteractiveExperiences' = @('license.txt')
    'Microsoft.Web.WebView2' = @('LICENSE.txt', 'NOTICE.txt')
}
$taskOutput = [IO.Path]::GetFullPath((Join-Path $PublishDirectory 'ThirdPartyNotices'))
$taskPublishRoot = [IO.Path]::GetFullPath($PublishDirectory) + [IO.Path]::DirectorySeparatorChar
if (-not $taskOutput.StartsWith($taskPublishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe notices output path.' }
if (Test-Path -LiteralPath $taskOutput) { Remove-Item -LiteralPath $taskOutput -Recurse -Force }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCopied = [Collections.Generic.List[object]]::new()
foreach ($taskPackage in $taskPackages.GetEnumerator()) {
    $taskParts = $taskPackage.Key -split '/', 2
    $taskPackagePath = Find-NoticePackage $taskPackage.Value
    if ($taskRequired.ContainsKey($taskParts[0])) {
        foreach ($taskName in $taskRequired[$taskParts[0]]) {
            if (-not (Test-Path -LiteralPath (Join-Path $taskPackagePath $taskName) -PathType Leaf)) {
                throw "Missing redistribution notice: $($taskPackage.Key)/$taskName"
            }
        }
    }
    # Root files only: tools/NOTICE.txt describes tooling, not the shipped runtime.
    $taskFiles = @(Get-ChildItem -LiteralPath $taskPackagePath -File | Where-Object {
        $_.Name -match '^(LICENSE(?:\.[^.]+)?|NOTICE(?:\.[^.]+)?|THIRD[-_]PARTY[-_]NOTICES(?:\.[^.]+)?|sdk_license\.txt)$'
    })
    if ($taskFiles.Count -eq 0) { continue }
    $taskTarget = Join-Path $taskOutput $taskPackage.Key
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in $taskFiles) { Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget }
    $taskCopied.Add([ordered]@{ Component = $taskPackage.Key; Files = @($taskFiles.Name) })
}
foreach ($taskRequiredId in $taskRequired.Keys) {
    if (@($taskPackages.Keys | Where-Object { ($_ -split '/', 2)[0] -eq $taskRequiredId }).Count -ne 1) {
        throw "Expected one resolved redistribution dependency: $taskRequiredId"
    }
}

# This targeting pack distributes WinRT.Runtime.dll but omits its upstream MIT text.
$taskWinRtPath = Join-Path $PublishDirectory 'WinRT.Runtime.dll'
$taskWinRtVersion = (Get-Item -LiteralPath $taskWinRtPath).VersionInfo.ProductVersion
if ($taskWinRtVersion -ne '2.2.0.48161+8649ee3eeb2445ca2a36d80d878ef60b96a6c65d') {
    throw "The pinned C#/WinRT license provenance must be reviewed for binary version $taskWinRtVersion."
}
$taskPinned = Join-Path $PSScriptRoot 'third-party/CsWinRT-2.2.0'
foreach ($taskName in @('LICENSE.txt', 'SOURCE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPinned $taskName) -PathType Leaf)) { throw "Missing pinned C#/WinRT $taskName." }
}
Copy-Item -LiteralPath $taskPinned -Destination (Join-Path $taskOutput 'CsWinRT-2.2.0') -Recurse
$taskCopied.Add([ordered]@{ Component = 'CsWinRT/2.2.0.48161'; Files = @('LICENSE.txt', 'SOURCE.txt') })

$taskSdkKey = @($taskDeps.libraries.Keys | Where-Object { $_ -like 'runtimepack.Microsoft.Windows.SDK.NET.Ref/*' })
if ($taskSdkKey.Count -ne 1) { throw 'Expected one Windows SDK .NET runtime pack for notice collection.' }
$taskSdkVersion = [version](($taskSdkKey[0] -split '/', 2)[1])
$taskSdkBuild = '{0}.{1}.{2}.0' -f $taskSdkVersion.Major, $taskSdkVersion.Minor, $taskSdkVersion.Build
$taskSdkLegal = Join-Path $PSScriptRoot "third-party/WindowsSDK-$taskSdkBuild"
$taskSdkTarget = Join-Path $taskOutput "WindowsSDK/$taskSdkBuild"
New-Item -ItemType Directory -Path $taskSdkTarget -Force | Out-Null
foreach ($taskName in @('sdk_license.rtf', 'sdk_third_party_notices.rtf', 'SOURCE.txt')) {
    $taskSource = Join-Path $taskSdkLegal $taskName
    if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { throw "Pinned Windows SDK $taskSdkBuild legal text is missing: $taskSource. Review and retain matching SDK legal texts." }
    Copy-Item -LiteralPath $taskSource -Destination $taskSdkTarget
}
$taskCopied.Add([ordered]@{ Component = $taskSdkKey[0]; Files = @("WindowsSDK/$taskSdkBuild/sdk_license.rtf", "WindowsSDK/$taskSdkBuild/sdk_third_party_notices.rtf", "WindowsSDK/$taskSdkBuild/SOURCE.txt") })
$taskCopied | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'components.json') -Encoding utf8
Write-Host "Redistribution notices: $taskOutput"
