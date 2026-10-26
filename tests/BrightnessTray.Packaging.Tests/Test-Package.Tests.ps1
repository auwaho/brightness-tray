Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskDirectory = Join-Path $taskRoot ('artifacts/packaging-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskCases = @(
    @{ Name = 'Valid'; Error = $null },
    @{ Name = 'ProductIdentity'; Error = $null },
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
    if ($taskCase.Name -eq 'ProductIdentity') {
        $taskManifest.Package.Identity.Name = 'Example.BrightnessTray'
        $taskManifest.Package.Identity.Publisher = 'CN=00000000-0000-0000-0000-000000000001'
        $taskManifest.Package.Properties.PublisherDisplayName = 'Example Publisher'
        $taskManifest.Package.Properties.DisplayName = 'Brightness Tray'
    }
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
        if ($taskResult.PackageName -ne $taskManifest.Package.Identity.Name -or
            $taskResult.Publisher -ne $taskManifest.Package.Identity.Publisher -or
            $taskResult.PublisherDisplayName -ne $taskManifest.Package.Properties.PublisherDisplayName -or
            $taskResult.DisplayName -ne $taskManifest.Package.Properties.DisplayName -or
            $taskResult.Architecture -ne 'x64' -or $taskResult.MinWindowsVersion -ne '10.0.22000.0') {
            throw 'Package result did not preserve the manifest product identity or platform.'
        }
        if ($taskCase.Name -eq 'ProductIdentity' -and $taskResult.UsesDevelopmentIdentity) {
            throw 'Configured product identity was reported as a development preview.'
        }
    } elseif ($null -eq $taskError -or -not $taskError.Contains($taskCase.Error, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Case $($taskCase.Name): expected '$($taskCase.Error)', got '$taskError'."
    }
    Write-Output "PASS Store packaging: $($taskCase.Name)"
}
Write-Output "$($taskCases.Count)/$($taskCases.Count) Store packaging checks passed."
$taskIdentityCases = @(
    @{ Name = 'MissingPublisher'; Arguments = @{ PackageName = 'Example.BrightnessTray'; PublisherDisplayName = 'Example Publisher' }; Error = 'Supply PackageName, Publisher and PublisherDisplayName' },
    @{ Name = 'MissingPackageName'; Arguments = @{ Publisher = 'CN=Example'; PublisherDisplayName = 'Example Publisher' }; Error = 'Supply PackageName, Publisher and PublisherDisplayName' },
    @{ Name = 'MissingPublisherDisplayName'; Arguments = @{ PackageName = 'Example.BrightnessTray'; Publisher = 'CN=Example' }; Error = 'Supply PackageName, Publisher and PublisherDisplayName' },
    @{ Name = 'DevelopmentPublisher'; Arguments = @{ PackageName = 'Example.BrightnessTray'; Publisher = 'CN=BrightnessTray Development'; PublisherDisplayName = 'Example Publisher' }; Error = 'must not use the development publisher' },
    @{ Name = 'BlankDisplayName'; Arguments = @{ DisplayName = ' ' }; Error = 'must not be blank' }
)
foreach ($taskCase in $taskIdentityCases) {
    $taskError = $null
    try {
        $taskArguments = $taskCase.Arguments
        & (Join-Path $taskRoot 'scripts/package-store.ps1') @taskArguments -SkipPublish | Out-Null
    } catch { $taskError = $_.Exception.Message }
    if ($null -eq $taskError -or -not $taskError.Contains($taskCase.Error, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Case $($taskCase.Name): expected '$($taskCase.Error)', got '$taskError'."
    }
    Write-Output "PASS Store identity preflight: $($taskCase.Name)"
}
Write-Output "$($taskIdentityCases.Count)/$($taskIdentityCases.Count) Store identity preflight checks passed."
& (Join-Path $PSScriptRoot 'Store-PublishMetadata.Tests.ps1')
