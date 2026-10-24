Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskDirectory = Join-Path $taskRoot ('artifacts/packaging-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskCases = @(
    @{ Name = 'Valid'; Error = $null },
    @{ Name = 'MissingFramework'; Error = 'framework dependency' },
    @{ Name = 'WrongPublisher'; Error = 'framework dependency' },
    @{ Name = 'OldFramework'; Error = 'framework dependency' },
    @{ Name = 'BundledWinUI'; Error = 'must not be bundled' },
    @{ Name = 'BundledWinUILowercase'; Error = 'must not be bundled' },
    @{ Name = 'MissingDotNet'; Error = 'Missing package file: coreclr.dll' },
    @{ Name = 'ExternalDotNet'; Error = '.NET runtime must remain self-contained' },
    @{ Name = 'SignedStore'; Error = 'must be unsigned' }
)
foreach ($taskCase in $taskCases) {
    [xml]$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'packaging/AppxManifest.xml') -Raw
    if ($taskCase.Name -ne 'MissingFramework') {
        $taskDependency = $taskManifest.CreateElement('PackageDependency', $taskManifest.DocumentElement.NamespaceURI)
        $taskDependency.SetAttribute('Name', 'Microsoft.WindowsAppRuntime.2')
        $taskDependency.SetAttribute('Publisher', $(if ($taskCase.Name -eq 'WrongPublisher') { 'CN=Wrong' } else {
            'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        }))
        $taskDependency.SetAttribute('MinVersion', $(if ($taskCase.Name -eq 'OldFramework') { '2.0.0.0' } else { '2.5.1.0' }))
        $taskManifest.Package.Dependencies.AppendChild($taskDependency) | Out-Null
    }
    $taskEntries = @{}
    foreach ($taskFile in @('BrightnessTray.exe', 'BrightnessTray.dll', 'BrightnessTray.pri', 'App.xbf', 'FlyoutWindow.xbf',
        'hostfxr.dll', 'coreclr.dll', 'Assets/StoreLogo.png', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png', 'Assets/Grain.png')) {
        $taskEntries[$taskFile] = 'fixture'
    }
    $taskEntries['AppxManifest.xml'] = $taskManifest.OuterXml
    $taskEntries['BrightnessTray.runtimeconfig.json'] = $(if ($taskCase.Name -eq 'ExternalDotNet') {
        '{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}'
    } else { '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}' })
    if ($taskCase.Name -eq 'MissingDotNet') { $taskEntries.Remove('coreclr.dll') }
    if ($taskCase.Name -eq 'BundledWinUI') { $taskEntries['Microsoft.UI.Xaml.dll'] = 'fixture' }
    if ($taskCase.Name -eq 'BundledWinUILowercase') { $taskEntries['Microsoft.ui.xaml.dll'] = 'fixture' }
    if ($taskCase.Name -eq 'SignedStore') { $taskEntries['AppxSignature.p7x'] = 'fixture' }
    $taskPath = Join-Path $taskDirectory ($taskCase.Name + '.msix')
    $taskZip = [IO.Compression.ZipFile]::Open($taskPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskEntry in $taskEntries.GetEnumerator()) {
            $taskWriter = [IO.StreamWriter]::new($taskZip.CreateEntry($taskEntry.Key).Open())
            try { $taskWriter.Write([string]$taskEntry.Value) } finally { $taskWriter.Dispose() }
        }
    } finally { $taskZip.Dispose() }
    $taskError = $null
    try { $taskResult = & (Join-Path $taskRoot 'packaging/Test-Package.ps1') -PackagePath $taskPath -Store }
    catch { $taskError = $_.Exception.Message }
    if ($null -eq $taskCase.Error) {
        if ($null -ne $taskError) { throw "Valid fixture rejected: $taskError" }
        if ($taskResult.Signed -or -not $taskResult.DotNetSelfContained) { throw 'Invalid Store result.' }
    } elseif ($null -eq $taskError -or -not $taskError.Contains($taskCase.Error, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Case $($taskCase.Name): expected '$($taskCase.Error)', got '$taskError'."
    }
    Write-Output "PASS Store packaging: $($taskCase.Name)"
}
Write-Output "$($taskCases.Count)/$($taskCases.Count) Store packaging checks passed."
& (Join-Path $PSScriptRoot 'Store-PublishMetadata.Tests.ps1')
