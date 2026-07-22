[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$projectDirectory = Join-Path $PSScriptRoot 'AudioStatusExtension'
$project = Join-Path $projectDirectory 'AudioStatusExtension.csproj'
$appPackages = Join-Path $projectDirectory 'AppPackages'
$stagingRoot = Join-Path $appPackages 'StoreBundleStaging'
$bundleInput = Join-Path $stagingRoot 'Input'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$makeAppx = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') `
    -Filter 'makeappx.exe' -Recurse |
    Where-Object FullName -Match '\\x64\\makeappx\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not (Test-Path $dotnet)) {
    throw ".NET SDK was not found at $dotnet."
}

if (-not $makeAppx) {
    throw 'MakeAppx.exe was not found in the Windows SDK.'
}

[xml]$projectXml = Get-Content $project
$version = ($projectXml.Project.PropertyGroup.AppxPackageVersion | Select-Object -First 1)
if (-not $version) {
    throw 'AppxPackageVersion is missing from the project file.'
}

if (Test-Path $stagingRoot) {
    Remove-Item $stagingRoot -Recurse -Force
}

New-Item $bundleInput -ItemType Directory -Force | Out-Null

function Publish-And-StageArchitecture {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [string]$Profile
    )

    $publishArguments = @('publish', $project, "-p:PublishProfile=$Profile", '--no-restore')
    $publishProcess = Start-Process -FilePath $dotnet -ArgumentList $publishArguments -Wait -NoNewWindow -PassThru
    if ($publishProcess.ExitCode -ne 0) {
        throw "$Architecture Store publish failed with exit code $($publishProcess.ExitCode)."
    }

    $upload = Get-ChildItem $appPackages -Filter "*_${Architecture}_bundle.msixupload" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $upload) {
        throw "The $Architecture publish did not create an .msixupload file."
    }

    $uploadExtract = Join-Path $stagingRoot "$Architecture-upload"
    $bundleExtract = Join-Path $stagingRoot "$Architecture-bundle"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($upload.FullName, $uploadExtract)

    $architectureBundle = Get-ChildItem $uploadExtract -Filter '*.msixbundle' | Select-Object -First 1
    [System.IO.Compression.ZipFile]::ExtractToDirectory($architectureBundle.FullName, $bundleExtract)
    $architecturePackage = Get-ChildItem $bundleExtract -Filter "*_${Architecture}.msix" | Select-Object -First 1
    Copy-Item $architecturePackage.FullName $bundleInput
}

Publish-And-StageArchitecture -Architecture 'x64' -Profile 'store-x64'
Publish-And-StageArchitecture -Architecture 'arm64' -Profile 'store-arm64'

$combinedBundleName = "AudioStatusExtension_${version}_x64_arm64.msixbundle"
$combinedBundle = Join-Path $stagingRoot $combinedBundleName
$bundleArguments = @('bundle', '/d', $bundleInput, '/p', $combinedBundle, '/bv', $version, '/o')
$bundleProcess = Start-Process -FilePath $makeAppx.FullName -ArgumentList $bundleArguments -Wait -NoNewWindow -PassThru
if ($bundleProcess.ExitCode -ne 0) {
    throw "Combined bundle creation failed with exit code $($bundleProcess.ExitCode)."
}

$finalUpload = Join-Path $appPackages "AudioStatusExtension_${version}_x64_arm64_bundle.msixupload"
$temporaryZip = [System.IO.Path]::ChangeExtension($finalUpload, '.zip')
Remove-Item $finalUpload, $temporaryZip -Force -ErrorAction SilentlyContinue

$uploadDirectory = Join-Path $stagingRoot 'Upload'
New-Item $uploadDirectory -ItemType Directory | Out-Null
Copy-Item $combinedBundle $uploadDirectory
Compress-Archive -Path (Join-Path $uploadDirectory '*') -DestinationPath $temporaryZip
Move-Item $temporaryZip $finalUpload

Write-Host "Created combined Store upload: $finalUpload"
Write-Host 'Upload this x64+ARM64 .msixupload file to Partner Center.'
