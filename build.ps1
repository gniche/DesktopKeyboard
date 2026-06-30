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

# --- Publish app (framework-dependent, ReadyToRun) ---------------------------
# Framework-dependent (dynamic linking) keeps the install compact: it links against
# the shared .NET Desktop Runtime instead of bundling it. The shared framework is
# already ReadyToRun on disk, and our own assembly is R2R-compiled here, so startup
# stays fast. Requires the .NET 9 Desktop Runtime to be installed on the target.

$publishDir = "$root\publish"
$rid        = "win-x64"

Write-Host "Publishing DesktopKeyboard ($Configuration, $rid, framework-dependent ReadyToRun)..." -ForegroundColor Cyan
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\DesktopKeyboard.csproj" `
    -c $Configuration `
    -r $rid `
    --self-contained false `
    -o $publishDir
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

    # Export public cert (.cer) into the publish folder so it's packaged with the app
    # and the installer can trust it system-wide (required for uiAccess + SmartScreen).
    $cerPath = "$publishDir\DesktopKeyboard.cer"
    $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cerPath, $certBytes)
    Write-Host "Exported cert to $cerPath" -ForegroundColor Green
}

# --- Sign the exe BEFORE packaging --------------------------------------------
# The installer bundles the exe as-is, and uiAccess="true" requires the *installed*
# exe to be signed. So the exe must be signed before wix packages it into the MSI,
# otherwise the MSI ships an unsigned exe and Windows rejects the launch with
# "A referral was returned from the server".

$exePath = "$publishDir\DesktopKeyboard.exe"

function Sign-Artifact($path) {
    Write-Host "Signing $path ..." -ForegroundColor Cyan
    $result = Set-AuthenticodeSignature `
        -FilePath        $path `
        -Certificate     $cert `
        -TimestampServer "http://timestamp.digicert.com" `
        -HashAlgorithm   SHA256
    if ($result.Status -notin "Valid", "UnknownError") {
        Write-Error "Signing failed for ${path} -- status: $($result.Status) -- $($result.StatusMessage)"
    }
    Write-Host "  Signed ($($result.Status))" -ForegroundColor Green
}

if ($SkipSign) {
    Write-Host "Skipping exe signing (-SkipSign)." -ForegroundColor Yellow
} else {
    Sign-Artifact $exePath
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

# --- Sign the installer ------------------------------------------------------

if ($SkipSign) {
    Write-Host "Skipping installer signing (-SkipSign)." -ForegroundColor Yellow
} else {
    Sign-Artifact "$outDir\DesktopKeyboard_Setup.msi"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Exe:       $publishDir\DesktopKeyboard.exe"
Write-Host "  Installer: $outDir\DesktopKeyboard_Setup.msi"
