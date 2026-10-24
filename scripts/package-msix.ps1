[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.1.3.0',
    [switch]$SkipPublish
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot 'artifacts/msix'
$taskSigning = Join-Path $taskRoot 'artifacts/signing'
$taskSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$taskSdk = Get-ChildItem -LiteralPath $taskSdkRoot -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path (Join-Path $_.FullName 'x64/makeappx.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $taskSdk) { throw 'Windows SDK with MakeAppx and SignTool is required.' }
$taskMakeAppx = Join-Path $taskSdk.FullName 'x64/makeappx.exe'
$taskSignTool = Join-Path $taskSdk.FullName 'x64/signtool.exe'
if (-not (Test-Path -LiteralPath $taskSignTool)) { throw 'SignTool is missing from the Windows SDK.' }
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$taskPublished = Join-Path $taskRoot 'artifacts/publish/win-x64'
if (-not (Test-Path (Join-Path $taskPublished 'BrightnessTray.exe'))) { throw 'Publish the application before packaging.' }
New-Item -ItemType Directory -Path $taskOutput, $taskSigning -Force | Out-Null
$taskStage = [IO.Path]::GetFullPath((Join-Path $taskOutput 'staging'))
$taskAllowed = [IO.Path]::GetFullPath($taskOutput) + [IO.Path]::DirectorySeparatorChar
if (-not $taskStage.StartsWith($taskAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
if (Test-Path -LiteralPath $taskStage) { Remove-Item -LiteralPath $taskStage -Recurse -Force }
New-Item -ItemType Directory -Path $taskStage | Out-Null
Get-ChildItem -LiteralPath $taskPublished | Copy-Item -Destination $taskStage -Recurse
& (Join-Path $taskRoot 'packaging/Create-Assets.ps1') -OutputDirectory (Join-Path $taskStage 'Assets')
$taskManifestPath = Join-Path $taskStage 'AppxManifest.xml'
[xml]$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'packaging/AppxManifest.xml') -Raw
$taskManifest.Package.Identity.Version = $Version
$taskManifest.Save($taskManifestPath)

# Keep the private key outside the distributable folder. Its password uses Windows DPAPI.
$taskPfx = Join-Path $taskSigning 'BrightnessTray.Development.pfx'
$taskPasswordFile = Join-Path $taskSigning 'BrightnessTray.Development.password.xml'
$taskCertificateFile = Join-Path $taskOutput 'BrightnessTray.Development.cer'
if (-not (Test-Path -LiteralPath $taskPfx)) {
    if (Test-Path -LiteralPath $taskPasswordFile) { throw 'The signing key is missing; restore it before building updates.' }
    $taskPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) |
        ConvertTo-SecureString -AsPlainText -Force
    $taskRsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $taskRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            [string]$taskManifest.Package.Identity.Publisher, $taskRsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $taskRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $taskRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $taskOids = [Security.Cryptography.OidCollection]::new()
        $taskOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $taskRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($taskOids, $true))
        $taskCertificate = $taskRequest.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddYears(2))
        try {
            $taskCredential = [PSCredential]::new('signing', $taskPassword)
            [IO.File]::WriteAllBytes($taskPfx, $taskCertificate.Export(
                [Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $taskCredential.GetNetworkCredential().Password))
            $taskPassword | Export-Clixml -LiteralPath $taskPasswordFile
        } finally { $taskCertificate.Dispose() }
    } finally { $taskRsa.Dispose() }
}
$taskPassword = Import-Clixml -LiteralPath $taskPasswordFile
$taskCredential = [PSCredential]::new('signing', $taskPassword)
$taskCertificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
    $taskPfx, $taskCredential.GetNetworkCredential().Password,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if ($taskCertificate.Subject -ne $taskManifest.Package.Identity.Publisher) { throw 'The signing certificate publisher does not match the manifest.' }
    if ($taskCertificate.NotAfter -le [DateTime]::Now) { throw 'The development signing certificate has expired.' }
    [IO.File]::WriteAllBytes($taskCertificateFile, $taskCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
} finally { $taskCertificate.Dispose() }
$taskPackage = Join-Path $taskOutput "BrightnessTray_${Version}_x64.msix"
$taskPackageLog = Join-Path $taskOutput 'makeappx.log'
& $taskMakeAppx pack /d $taskStage /p $taskPackage /o *> $taskPackageLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $taskPackageLog -Tail 20 | Write-Host
    throw 'MSIX validation or packaging failed.'
}
& $taskSignTool sign /fd SHA256 /f $taskPfx /p $taskCredential.GetNetworkCredential().Password $taskPackage
if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
& (Join-Path $taskRoot 'packaging/Test-Package.ps1') -PackagePath $taskPackage -CertificatePath $taskCertificateFile |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'validation.json')
Write-Host "Package: $taskPackage"
Write-Host "Development certificate: $taskCertificateFile"
