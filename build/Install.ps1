# Installs Berth from the files beside this script: Berth.cer and a Berth_<version>_<arch>.msix.
# Run it as yourself (right-click > Run with PowerShell). Nothing is downloaded.
# -NoPause skips the final "Press Enter" (for running it from a terminal or a script).
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'

try {
    $cerPath = Join-Path $PSScriptRoot 'Berth.cer'
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $packages = Get-ChildItem $PSScriptRoot -Filter 'Berth_*.msix'
    # This machine's own architecture first; an x64 package also runs on ARM64.
    $msix = @($packages | Where-Object Name -like "*_$arch.msix") + @($packages | Where-Object Name -like '*_x64.msix') |
        Select-Object -First 1
    if (-not (Test-Path $cerPath)) { throw "Berth.cer is not next to this script ($PSScriptRoot)." }
    if (-not $msix) { throw "No Berth_*_$arch.msix or Berth_*_x64.msix next to this script ($PSScriptRoot)." }

    # 1. Trust the publisher's certificate (once per computer). Berth is signed with a self-signed certificate,
    #    so Windows installs it only when that certificate is in the computer's Trusted People store.
    $cer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cer.Thumbprint
    if ($trusted) {
        Write-Host "The certificate $($cer.Subject) ($($cer.Thumbprint)) is already trusted."
    } else {
        Write-Host "Berth is signed with a self-signed certificate: $($cer.Subject), thumbprint $($cer.Thumbprint)."
        Write-Host "The next step adds it to this computer's Trusted People store, so Windows accepts the package."
        Write-Host "That store belongs to the computer, so Windows asks for administrator approval for this one step."
        Read-Host 'Press Enter to continue, or close this window to stop'
        $import = "Import-Certificate -FilePath '$($cerPath.Replace("'", "''"))' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $import
        if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cer.Thumbprint)) {
            throw 'The certificate was not added (the administrator prompt was declined or failed).'
        }
        Write-Host 'Certificate trusted.'
    }

    # 2. Install (or update) Berth for this user. A running Berth is closed first.
    Write-Host "Installing $($msix.Name)..."
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
    Write-Host 'Berth is installed. Start it from the Start menu.'
} catch {
    Write-Host "Berth was not installed: $($_.Exception.Message)" -ForegroundColor Red
}
if (-not $NoPause) { Read-Host 'Press Enter to close' }
