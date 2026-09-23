[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64')]
    [string]$Architecture = 'arm64'
)

$ErrorActionPreference = 'Stop'

Write-Host "=== Audio Status Extension Local Installer ($Architecture) ===" -ForegroundColor Cyan

$projectDirectory = Join-Path $PSScriptRoot 'AudioStatusExtension'
$project = Join-Path $projectDirectory 'AudioStatusExtension.csproj'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$profile = "store-$Architecture"

if (-not (Test-Path $dotnet)) {
    throw ".NET SDK was not found at $dotnet."
}

# 1. Locate signtool.exe
$signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Match "\\$Architecture\\signtool\.exe$" |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $signtool) {
    # Fallback to any signtool.exe
    $signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}

if (-not $signtool) {
    throw "signtool.exe was not found in the Windows SDK."
}

# 2. Build & Publish the MSIX package
Write-Host "Publishing project with profile $profile..." -ForegroundColor Yellow
& $dotnet publish $project "-p:PublishProfile=$profile" -p:UseSharedCompilation=false -nodereuse:false
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

# 3. Locate generated .msix package
$msix = Get-ChildItem (Join-Path $projectDirectory "bin\Release\net10.0-windows10.0.26100.0\win-$Architecture") -Filter "*_$Architecture.msix" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msix) {
    # Also check AppPackages
    $msix = Get-ChildItem (Join-Path $projectDirectory "AppPackages") -Filter "*_$Architecture.msix" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $msix) {
    throw "MSIX package for $Architecture was not found."
}
Write-Host "Found MSIX package: $($msix.FullName)" -ForegroundColor Green

# 4. Prepare / find developer certificate
$subject = "CN=3EAEAC55-63BF-4FAD-B765-EC11C364E23F"
$cert = Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1

if (-not $cert) {
    Write-Host "Generating self-signed developer certificate ($subject)..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "AudioStatusDevCert" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}

$certFile = Join-Path $PSScriptRoot "AudioStatusDevCert.cer"
if (-not (Test-Path $certFile)) {
    Export-Certificate -Cert $cert -FilePath $certFile | Out-Null
    Write-Host "Exported certificate to $certFile" -ForegroundColor Green
}

# 5. Check if certificate is installed in LocalMachine\TrustedPeople
$isTrusted = Get-ChildItem "Cert:\LocalMachine\TrustedPeople" -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint }

# 6. Sign the MSIX package
Write-Host "Signing MSIX package with developer certificate..." -ForegroundColor Yellow
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $msix.FullName
if ($LASTEXITCODE -ne 0) {
    throw "Failed to sign MSIX package with signtool.exe."
}
Write-Host "Package signed successfully." -ForegroundColor Green

# 7. Install package
if ($isTrusted) {
    Write-Host "Certificate is trusted in LocalMachine\TrustedPeople. Installing MSIX package..." -ForegroundColor Yellow
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Write-Host "Package installed successfully!" -ForegroundColor Green
} else {
    # Check if running as administrator
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        Write-Host "Running as Administrator. Importing certificate into LocalMachine\TrustedPeople..." -ForegroundColor Yellow
        Import-Certificate -FilePath $certFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        Write-Host "Certificate imported successfully." -ForegroundColor Green
        Write-Host "Installing MSIX package..." -ForegroundColor Yellow
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
        Write-Host "Package installed successfully!" -ForegroundColor Green
    } else {
        # Check if Developer Mode is enabled for loose layout registration
        $devMode = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name "AllowDevelopmentWithoutDevLicense" -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
        if ($devMode -eq 1) {
            Write-Host "Developer Mode is enabled! Registering package layout..." -ForegroundColor Yellow
            $manifest = Join-Path $projectDirectory "bin\Release\net10.0-windows10.0.26100.0\win-$Architecture\AppxManifest.xml"
            Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ForceUpdateFromAnyVersion
            Write-Host "Package registered successfully via Developer Mode!" -ForegroundColor Green
        } else {
            Write-Host "`n[Action Required] To install a local developer package on Windows, either:" -ForegroundColor Yellow
            Write-Host "  1. Right-click 'Trust-DevCert.cmd' in this folder and choose 'Run as administrator'." -ForegroundColor Cyan
            Write-Host "     (This imports the local dev certificate into Trusted People and completes the install)"
            Write-Host "  OR"
            Write-Host "  2. Enable Windows Developer Mode in Settings -> System -> For developers -> Developer Mode -> On," -ForegroundColor Cyan
            Write-Host "     then run .\Install-Arm64.ps1 again.`n"
            return
        }
    }
}

# 8. Check installation status
$pkg = Get-AppxPackage -Name "*AudioStatus*" | Select-Object -First 1
if ($pkg) {
    Write-Host "`nInstalled Package Details:" -ForegroundColor Cyan
    Write-Host "  Name:    $($pkg.Name)"
    Write-Host "  Version: $($pkg.Version)"
    Write-Host "  Arch:    $($pkg.Architecture)"
    Write-Host "  Status:  $($pkg.Status)"
    Write-Host "`nThe extension is now registered with Windows and PowerToys Command Palette." -ForegroundColor Green
}

