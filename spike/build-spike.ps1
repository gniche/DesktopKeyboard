param([switch]$SkipSign)

# Publishes the Avalonia spike, signs it with the same cert the main app uses (already
# trusted in LocalMachine\Root once the main app has been installed), and copies it to
# Program Files so uiAccess is honored. Then launch it and tail the perf log.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pub  = "$root\publish"

Write-Host "Publishing spike (framework-dependent, R2R)..." -ForegroundColor Cyan
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\Spike.csproj" -c Release -r win-x64 --self-contained false -o $pub
if (-not $?) { exit 1 }

$exe = "$pub\DesktopKeyboardSpike.exe"

if (-not $SkipSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Subject -eq "CN=DesktopKeyboard" } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "CN=DesktopKeyboard cert not found. Run the main build.ps1 once so the cert exists and is trusted." }
    Write-Host "Signing with $($cert.Thumbprint)..." -ForegroundColor Cyan
    $r = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
    if ($r.Status -notin "Valid","UnknownError") { throw "Signing failed: $($r.Status) $($r.StatusMessage)" }
}

$dest = "$env:ProgramFiles\DesktopKeyboardSpike"
Write-Host "Installing to $dest (elevation prompt)..." -ForegroundColor Cyan
$copy = "Remove-Item '$dest' -Recurse -Force -ErrorAction SilentlyContinue; " +
        "New-Item -ItemType Directory -Force '$dest' | Out-Null; " +
        "Copy-Item '$pub\*' '$dest' -Recurse -Force"
Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile","-Command",$copy

Write-Host "Done." -ForegroundColor Green
Write-Host "Launch:  & '$dest\DesktopKeyboardSpike.exe'"
Write-Host "Perf log: `$env:TEMP\DesktopKeyboardSpike_perf.log"
