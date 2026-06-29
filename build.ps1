param(
    [string]$Configuration = "Release",
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
$root        = $PSScriptRoot
$certSubject = "CN=DesktopKeyboard"
$certStore   = "Cert:\CurrentUser\My"

# dotnet global tools may not be on PATH in all shells
$dotnetToolsPath = Join-Path $env:USERPROFILE ".dotnet\tools"
if ($env:PATH -notlike "*$dotnetToolsPath*") {
    $env:PATH = "$dotnetToolsPath;$env:PATH"
}

# --- Build app ---------------------------------------------------------------

Write-Host "Building DesktopKeyboard ($Configuration)..." -ForegroundColor Cyan
dotnet build "$root\DesktopKeyboard.csproj" -c $Configuration
if (-not $?) { exit 1 }

# --- Cert (create if missing, export for installer) --------------------------

if (-not $SkipSign) {
    $cert = Get-ChildItem $certStore -CodeSigningCert |
            Where-Object { $_.Subject -eq $certSubject } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

    if ($cert) {
        Write-Host "Using existing cert: $($cert.Thumbprint) (expires $($cert.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Cyan
    } else {
        Write-Host "Creating self-signed code-signing certificate..." -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate `
            -Type              CodeSigningCert `
            -Subject           $certSubject `
            -CertStoreLocation $certStore `
            -KeyExportPolicy   Exportable `
            -NotAfter          (Get-Date).AddYears(5)
        Write-Host "Created cert: $($cert.Thumbprint)" -ForegroundColor Green
    }

    # Export public cert (.cer) for the installer to bundle and trust system-wide.
    $cerPath = "$root\DesktopKeyboard.Installer\DesktopKeyboard.cer"
    $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cerPath, $certBytes)
    Write-Host "Exported cert to $cerPath" -ForegroundColor Green
}

# --- Build installer ---------------------------------------------------------

Write-Host "Building installer..." -ForegroundColor Cyan
$outDir = "$root\DesktopKeyboard.Installer\bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

wix build "$root\DesktopKeyboard.Installer\Package.wxs" `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -b "$root\DesktopKeyboard.Installer" `
    -o "$outDir\DesktopKeyboard_Setup.msi"
if (-not $?) { exit 1 }

# --- Sign artifacts ----------------------------------------------------------

if ($SkipSign) {
    Write-Host "Skipping signing (-SkipSign)." -ForegroundColor Yellow
} else {
    $artifacts = @(
        "$root\bin\$Configuration\net9.0-windows\DesktopKeyboard.exe",
        "$outDir\DesktopKeyboard_Setup.msi"
    )

    foreach ($artifact in $artifacts) {
        Write-Host "Signing $artifact ..." -ForegroundColor Cyan
        $result = Set-AuthenticodeSignature `
            -FilePath        $artifact `
            -Certificate     $cert `
            -TimestampServer "http://timestamp.digicert.com" `
            -HashAlgorithm   SHA256
        if ($result.Status -notin "Valid", "UnknownError") {
            Write-Error "Signing failed for ${artifact} -- status: $($result.Status) -- $($result.StatusMessage)"
        }
        Write-Host "  Signed ($($result.Status))" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Exe:       $root\bin\$Configuration\net9.0-windows\DesktopKeyboard.exe"
Write-Host "  Installer: $outDir\DesktopKeyboard_Setup.msi"
