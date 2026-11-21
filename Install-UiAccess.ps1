#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installiert und signiert ZoomHost mit offiziellem Windows UIAccess (uiAccess="true")
    unter "C:\Program Files\ZoomHost", damit die Vektor-Lupe auch ueber Startmenue,
    Windows-Suche, Taskleiste und Admin-Fenstern aktiv bleibt (kein Bitmap-Fallback).
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projPath = Join-Path $repoRoot 'ZoomHost\ZoomHost.csproj'
$installDir = Join-Path $env:ProgramFiles 'ZoomHost'
$certSubject = 'CN=ZoomHost Local UIAccess'

$dotnetExe = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnetExe)) {
    $dotnetExe = 'dotnet'
}

Write-Host '1/4 Stoppe laufende ZoomHost-Instanzen...' -ForegroundColor Cyan
Stop-Process -Name ZoomHost -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

Write-Host '2/4 Baue self-contained ZoomHost mit uiAccess="true" (Release win-x64)...' -ForegroundColor Cyan
$stageDir = Join-Path $repoRoot 'ZoomHost\bin\x64\Release-UiAccess'
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
& $dotnetExe publish $projPath -c Release -r win-x64 --self-contained true -p:EnableUiAccess=true -o $stageDir
if ($LASTEXITCODE -ne 0) {
    throw "Publish fehlgeschlagen (ExitCode $LASTEXITCODE)."
}

$stagedExe = Join-Path $stageDir 'ZoomHost.exe'
$exeText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($stagedExe))
if (-not $exeText.Contains('uiAccess="true"')) {
    throw "Fehler: uiAccess='true' Manifest wurde nicht in $stagedExe eingebettet."
}

Write-Host '3/4 Erstelle bzw. pruefe lokales Code-Signing-Zertifikat...' -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq $certSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubject `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(5)
}

$tmpCer = Join-Path $env:TEMP 'ZoomHost_Local_UIAccess.cer'
Export-Certificate -Cert $cert -FilePath $tmpCer -Force | Out-Null
try {
    foreach ($store in @('Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPublisher')) {
        $exists = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
        if (-not $exists) {
            Import-Certificate -FilePath $tmpCer -CertStoreLocation $store | Out-Null
        }
    }
} finally {
    Remove-Item $tmpCer -Force -ErrorAction SilentlyContinue
}

Write-Host "4/4 Kopiere nach '$installDir' und signiere ZoomHost.exe..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path (Join-Path $stageDir '*') -Destination $installDir -Recurse -Force

$exePath = Join-Path $installDir 'ZoomHost.exe'
$sig = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid') {
    throw "Signierung nicht gueltig: $($sig.Status) - $($sig.StatusMessage)"
}

$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop 'ZoomHost (Vektor + Startmenue).lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = $exePath
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'ZoomHost mit UIAccess (Vektor-Rendering auch ueber Startmenue & Suche)'
$lnk.Save()

Write-Host ''
Write-Host "Fertig! Starte ZoomHost kuenftig ueber:" -ForegroundColor Green
Write-Host "  $exePath" -ForegroundColor White
Write-Host "  oder Desktop-Verknuepfung: $lnkPath" -ForegroundColor White
