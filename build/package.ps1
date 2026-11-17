# Builds Berth's MSIX packages (trimmed Release, .NET and the Windows App SDK self-contained), signs them with a
# code-signing certificate from Cert:\CurrentUser\My, and writes the release files to artifacts\:
#   Berth_<version>_<arch>.msix, Berth.cer (the public certificate only) and Install.ps1.
# The certificate is chosen by thumbprint or subject; no key or password is ever passed or written.
#
#   .\build\package.ps1                                  # x64 and arm64, certificate CN=Koderi
#   .\build\package.ps1 -Certificate 1D9E99...           # a certificate by thumbprint
#   .\build\package.ps1 -Platforms x64
param(
    [string]$Certificate = 'CN=Koderi',
    [ValidateSet('x64', 'arm64')][string[]]$Platforms = @('x64', 'arm64'),
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Berth.App\Berth.App.csproj'
$artifacts = Join-Path $root 'artifacts'

# The package's identity, from the manifest: the certificate's subject must equal its Publisher.
[xml]$manifest = Get-Content (Join-Path $root 'src\Berth.App\Package.appxmanifest')
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version

$codeSigning = '1.3.6.1.5.5.7.3.3'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { ($_.Thumbprint -eq $Certificate -or $_.Subject -eq $Certificate) -and $_.HasPrivateKey -and
                   $_.NotAfter -gt (Get-Date) -and ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigning) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) { throw "No valid code-signing certificate '$Certificate' with a private key in Cert:\CurrentUser\My." }
if ($cert.Subject -ne $publisher) { throw "The certificate's subject '$($cert.Subject)' is not the manifest's Publisher '$publisher'." }

# signtool from the newest Windows SDK.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK.' }

New-Item -ItemType Directory -Force $artifacts | Out-Null
foreach ($platform in $Platforms) {
    $build = Join-Path $artifacts "build-$platform\"
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
    Write-Host "Building the $platform package..."
    dotnet publish $project -c Release -r "win-$platform" -p:Platform=$platform -p:SelfContained=true `
        -p:WindowsAppSDKSelfContained=true -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=$build `
        -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $platform." }

    $built = Get-ChildItem $build -Recurse -Filter '*.msix' | Select-Object -First 1
    $msix = Join-Path $artifacts "Berth_${version}_$platform.msix"
    Copy-Item $built.FullName $msix -Force
    Remove-Item $build -Recurse -Force

    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /tr $TimestampUrl /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "Signing $msix failed." }
    Write-Host ('{0}: {1:N1} MB' -f (Split-Path $msix -Leaf), ((Get-Item $msix).Length / 1MB))
}

Export-Certificate -Cert $cert -FilePath (Join-Path $artifacts 'Berth.cer') -Type CERT | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1') $artifacts -Force
Write-Host "Done: $artifacts (certificate $($cert.Thumbprint), $($cert.Subject))"
