[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$CertificatePath,
    [switch]$Store
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$taskZip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
function Find-PackageEntry([string]$Name) {
    $taskZip.Entries | Where-Object { $_.FullName.Equals($Name, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
}
$taskPublic = $null
$taskSigner = $null
try {
    if ($Store) {
        if (Find-PackageEntry 'AppxSignature.p7x') { throw 'The Store submission package must be unsigned.' }
    } else {
        if ([string]::IsNullOrWhiteSpace($CertificatePath)) { throw 'A certificate is required to validate the standalone package.' }
        $taskPublic = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($CertificatePath)
        $taskSignature = (Find-PackageEntry 'AppxSignature.p7x').Open()
        $taskBuffer = [IO.MemoryStream]::new()
        try { $taskSignature.CopyTo($taskBuffer); $taskBytes = $taskBuffer.ToArray() }
        finally { $taskSignature.Dispose(); $taskBuffer.Dispose() }
        if ([Text.Encoding]::ASCII.GetString($taskBytes, 0, 4) -ne 'PKCX') { throw 'Invalid MSIX signature header.' }
        $taskCms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $taskCms.Decode([byte[]]$taskBytes[4..($taskBytes.Length - 1)])
        # Validate cryptography without adding the development certificate to Windows trust.
        $taskCms.CheckSignature($true)
        $taskSigner = $taskCms.SignerInfos[0].Certificate
        if ($taskSigner.Thumbprint -ne $taskPublic.Thumbprint) { throw 'The supplied certificate does not match the package signer.' }
    }
    $taskReader = [IO.StreamReader]::new((Find-PackageEntry 'AppxManifest.xml').Open())
    try { [xml]$taskManifest = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
    if (-not $Store -and $taskSigner.Subject -ne $taskManifest.Package.Identity.Publisher) { throw 'Manifest publisher differs from the signer.' }
    foreach ($taskRequired in @('BrightnessTray.exe', 'BrightnessTray.dll', 'BrightnessTray.pri', 'App.xbf',
        'FlyoutWindow.xbf', 'hostfxr.dll', 'coreclr.dll', 'BrightnessTray.runtimeconfig.json',
        'Assets/StoreLogo.png', 'Assets/Square44x44Logo.png', 'Assets/Square150x150Logo.png', 'Assets/Grain.png')) {
        if (-not (Find-PackageEntry $taskRequired)) {
            throw "Missing package file: $taskRequired"
        }
    }
    $taskFrameworkDependency = $null
    if ($Store) {
        $taskDependencies = @($taskManifest.SelectNodes('/*[local-name()="Package"]/*[local-name()="Dependencies"]/*[local-name()="PackageDependency"]'))
        if ($taskDependencies.Count -ne 1 -or $taskDependencies[0].Name -ne 'Microsoft.WindowsAppRuntime.2' -or
            $taskDependencies[0].Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
            $taskManifest.Package.Identity.ProcessorArchitecture -ne 'x64' -or [version]$taskDependencies[0].MinVersion -lt [version]'2.5.1.0') {
            throw 'Missing or incompatible Windows App Runtime framework dependency.'
        }
        foreach ($taskSharedFile in @('Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.UI.Input.dll')) {
            if (Find-PackageEntry $taskSharedFile) { throw "The shared WinUI runtime must not be bundled: $taskSharedFile" }
        }
        $taskFrameworkDependency = [pscustomobject]@{
            Name = $taskDependencies[0].Name
            MinVersion = $taskDependencies[0].MinVersion
        }
    } elseif (-not (Find-PackageEntry 'Microsoft.UI.Xaml.dll')) { throw 'The standalone WinUI runtime is missing.' }
    $taskReader = [IO.StreamReader]::new((Find-PackageEntry 'BrightnessTray.runtimeconfig.json').Open())
    try { $taskRuntime = $taskReader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $taskReader.Dispose() }
    if ($taskRuntime.runtimeOptions.ContainsKey('framework') -or $taskRuntime.runtimeOptions.ContainsKey('frameworks') -or
        -not $taskRuntime.runtimeOptions.ContainsKey('includedFrameworks')) { throw 'The .NET runtime must remain self-contained.' }
    # Guard against publishing over a stale folder or restoring the aggregate SDK by accident.
    foreach ($taskUnused in @('DirectML.dll', 'onnxruntime.dll', 'Microsoft.Windows.Search.dll',
        'Microsoft.Windows.Widgets.dll', 'Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll')) {
        if (Find-PackageEntry $taskUnused) { throw "Unused SDK component in the brightness-only package: $taskUnused" }
    }
    $taskStartup = $taskManifest.Package.Applications.Application.Extensions.Extension.StartupTask
    if ($taskStartup.TaskId -ne 'BrightnessTrayStartup' -or $taskStartup.Enabled -ne 'false') { throw 'Invalid startup declaration.' }
    [pscustomobject]@{
        Deployment = $(if ($Store) { 'Store' } else { 'Standalone' })
        Signed = -not $Store
        SignatureMathValid = $(if ($Store) { $null } else { $true })
        Signer = $(if ($Store) { $null } else { $taskSigner.Subject })
        UsesDevelopmentIdentity = $taskManifest.Package.Identity.Publisher -eq 'CN=BrightnessTray Development'
        WindowsAppRuntimeDependency = $taskFrameworkDependency
        DotNetSelfContained = $true
        PackageName = $taskManifest.Package.Identity.Name
        Publisher = $taskManifest.Package.Identity.Publisher
        PublisherDisplayName = $taskManifest.Package.Properties.PublisherDisplayName
        DisplayName = $taskManifest.Package.Properties.DisplayName
        Architecture = $taskManifest.Package.Identity.ProcessorArchitecture
        MinWindowsVersion = $taskManifest.Package.Dependencies.TargetDeviceFamily.MinVersion
        Version = $taskManifest.Package.Identity.Version
        EntryCount = $taskZip.Entries.Count
        Bytes = (Get-Item -LiteralPath $PackagePath).Length
        UncompressedBytes = ($taskZip.Entries | Measure-Object Length -Sum).Sum
        UnusedSdkComponentsIncluded = $false
        StartupTaskEnabled = $false
        CertificateTrustedAutomatically = $false
    }
} finally { if ($null -ne $taskPublic) { $taskPublic.Dispose() }; $taskZip.Dispose() }
