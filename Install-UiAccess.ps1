<#
.SYNOPSIS
    Baut, staget und installiert ZoomHost self-contained mit Windows UIAccess (uiAccess="true")
    inklusive aller 9 vorkompilierten O3-Shader im ShaderCache unter "C:\Program Files\ZoomHost",
    damit die Vektor-Lupe auch ueber Startmenue, Windows-Suche, Taskleiste und Admin-Fenstern
    aktiv bleibt (kein Bitmap-Fallback). Die portablen USB-Builds bleiben davon unberuehrt.

.PARAMETER StageOnly
    Baut nur den Staging-Ordner "ZoomHost\bin\x64\Release-UiAccess" inkl. uiAccess="true"
    Manifest-Pruefung und ShaderCache (benoetigt keine Administratorrechte).

.PARAMETER Uninstall
    Entfernt die installierte UIAccess-Version, die Desktop-Verknuepfung sowie das lokale
    Code-Signing-Zertifikat "CN=ZoomHost Local UIAccess".

.PARAMETER NoPause
    Unterdrueckt die Eingabeaufforderung am Ende bei automatischer UAC-Hochstufung.
#>
[CmdletBinding()]
param(
    [switch]$StageOnly,
    [switch]$Uninstall,
    [switch]$NoPause,
    [switch]$PauseOnExit
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptFilePath = $PSCommandPath
if (-not $scriptFilePath) {
    $scriptFilePath = $MyInvocation.MyCommand.Path
}
$projPath = Join-Path $repoRoot 'ZoomHost\ZoomHost.csproj'
$hlslPath = Join-Path $repoRoot 'ZoomHost\Shaders\EdgeSmoothing.hlsl'
$stageDir = Join-Path $repoRoot 'ZoomHost\bin\x64\Release-UiAccess'
$installDir = Join-Path $env:ProgramFiles 'ZoomHost'
$certSubject = 'CN=ZoomHost Local UIAccess'
$shortcutName = 'ZoomHost (Vektor + Startmenue).lnk'

function Test-IsAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-SelfElevateIfNeeded {
    if ($StageOnly -or (Test-IsAdministrator)) {
        return
    }

    Write-Host 'Fuer die Installation/Deinstallation unter "C:\Program Files\ZoomHost" sind Administratorrechte erforderlich. Starte UAC-Abfrage...' -ForegroundColor Yellow

    $argParts = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $scriptFilePath)
    )
    if ($Uninstall) {
        $argParts += '-Uninstall'
    }
    if ($NoPause) {
        $argParts += '-NoPause'
    } else {
        $argParts += '-PauseOnExit'
    }

    Start-Process -FilePath 'powershell.exe' -ArgumentList ($argParts -join ' ') -Verb RunAs | Out-Null
    exit 0
}

function Stop-RunningZoomHost {
    $procs = Get-Process -Name 'ZoomHost' -ErrorAction SilentlyContinue
    if ($procs) {
        Stop-Process -Name 'ZoomHost' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
}

function Remove-UiAccessInstallation {
    Write-Host '1/3 Stoppe laufende ZoomHost-Instanzen...' -ForegroundColor Cyan
    Stop-RunningZoomHost

    Write-Host "2/3 Entferne '$installDir' und Desktop-Verknuepfung..." -ForegroundColor Cyan
    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
    }

    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnkPath = Join-Path $desktop $shortcutName
    if (Test-Path $lnkPath) {
        Remove-Item $lnkPath -Force
    }

    Write-Host "3/3 Entferne lokales Zertifikat '$certSubject'..." -ForegroundColor Cyan
    foreach ($storePath in @('Cert:\LocalMachine\My', 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPublisher')) {
        Get-ChildItem $storePath -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $certSubject } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host 'UIAccess-Installation wurde vollstaendig entfernt. Portable USB-Builds blieben unveraendert.' -ForegroundColor Green
}

function Get-HlslCacheHash {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        throw "HLSL-Shaderdatei nicht gefunden: $Path"
    }

    $source = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("O3|" + $source)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToUpperInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Copy-PrecompiledShaderCache {
    param([Parameter(Mandatory = $true)][string]$TargetRootDir)

    $hash = Get-HlslCacheHash -Path $hlslPath
    $shaderNames = @('vs', 'vscrop', 'psprepcolor', 'psprepmeta', 'pspole', 'psmediamask', 'ps', 'vscursor', 'pscursor')

    $candidateDirs = @(
        (Join-Path $repoRoot 'ZoomHost\bin\x64\Release\net8.0-windows\win-x64\ShaderCache'),
        (Join-Path $repoRoot 'ZoomHost\bin\x64\Release-Updated\ShaderCache'),
        (Join-Path $repoRoot 'ZoomHost\bin\Release\net8.0-windows\win-x64\ShaderCache'),
        (Join-Path $env:LOCALAPPDATA 'ZoomText_Clone\ShaderCache')
    )

    $matchedSourceDir = $null
    $matchedFiles = $null

    foreach ($dir in $candidateDirs) {
        if (-not (Test-Path $dir)) {
            continue
        }

        $files = @()
        $allPresent = $true
        foreach ($name in $shaderNames) {
            $filePath = Join-Path $dir ("{0}_{1}.cso" -f $name, $hash)
            if (-not (Test-Path $filePath)) {
                $allPresent = $false
                break
            }
            $item = Get-Item $filePath
            if ($item.Length -le 0) {
                $allPresent = $false
                break
            }
            $files += $item
        }

        if ($allPresent) {
            $matchedSourceDir = $dir
            $matchedFiles = $files
            break
        }
    }

    if ($matchedSourceDir -and $matchedFiles) {
        $targetShaderDir = Join-Path $TargetRootDir 'ShaderCache'
        New-Item -ItemType Directory -Path $targetShaderDir -Force | Out-Null
        foreach ($file in $matchedFiles) {
            Copy-Item -Path $file.FullName -Destination (Join-Path $targetShaderDir $file.Name) -Force
        }
        Write-Host ("  -> {0} vorkompilierte O3-Shader (Hash {1}) aus '{2}' uebernommen." -f $matchedFiles.Count, $hash.Substring(0, 12), $matchedSourceDir) -ForegroundColor Gray
        return $matchedFiles.Count
    }

    Write-Warning ("Kein vollstaendiger 9er-O3-ShaderCache fuer Hash {0} gefunden. Beim ersten Start von ZoomHost werden die Shader einmalig kompiliert und unter '%LocalAppData%\ZoomText_Clone\ShaderCache' gespeichert." -f $hash)
    return 0
}

try {
    Invoke-SelfElevateIfNeeded

    if ($Uninstall) {
        Remove-UiAccessInstallation
    } else {
        $dotnetExe = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
        if (-not (Test-Path $dotnetExe)) {
            $dotnetExe = 'dotnet'
        }

        if (-not $StageOnly) {
            Write-Host '1/5 Stoppe laufende ZoomHost-Instanzen...' -ForegroundColor Cyan
            Stop-RunningZoomHost
        } else {
            Write-Host '1/3 StageOnly-Modus: laufende ZoomHost-Instanzen bleiben unberuehrt.' -ForegroundColor Cyan
        }

        $buildStepLabel = if ($StageOnly) { '2/3' } else { '2/5' }
        Write-Host "$buildStepLabel Baue self-contained ZoomHost mit uiAccess=`"true`" (Release win-x64)..." -ForegroundColor Cyan
        $uiAccessTempBin = Join-Path $repoRoot 'ZoomHost\bin\UiAccess'
        $objRoot = Join-Path $repoRoot 'ZoomHost\obj'
        $clearObjManifestOutputs = {
            if (Test-Path $objRoot) {
                Get-ChildItem -Path $objRoot -Recurse -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -in @('ZoomHost.dll', 'apphost.exe') } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
        if (Test-Path $stageDir) {
            Remove-Item $stageDir -Recurse -Force
        }
        & $clearObjManifestOutputs
        try {
            & $dotnetExe publish $projPath -c Release -r win-x64 --self-contained true -p:EnableUiAccess=true '-p:BaseOutputPath=bin/UiAccess/' -o $stageDir
            if ($LASTEXITCODE -ne 0) {
                throw "Publish fehlgeschlagen (ExitCode $LASTEXITCODE)."
            }
        } finally {
            if (Test-Path $uiAccessTempBin) {
                Remove-Item $uiAccessTempBin -Recurse -Force -ErrorAction SilentlyContinue
            }
            & $clearObjManifestOutputs
        }

        $stagedExe = Join-Path $stageDir 'ZoomHost.exe'
        $exeText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($stagedExe))
        if (-not $exeText.Contains('uiAccess="true"')) {
            throw "Fehler: uiAccess='true' Manifest wurde nicht in $stagedExe eingebettet."
        }

        $cacheStepLabel = if ($StageOnly) { '3/3' } else { '3/5' }
        Write-Host "$cacheStepLabel Kopiere vorkompilierten O3-ShaderCache (9 .cso-Dateien)..." -ForegroundColor Cyan
        $copiedShaderCount = Copy-PrecompiledShaderCache -TargetRootDir $stageDir

        if ($StageOnly) {
            Write-Host ''
            Write-Host 'StageOnly erfolgreich abgeschlossen!' -ForegroundColor Green
            Write-Host "  Staging-Pfad:   $stageDir" -ForegroundColor White
            Write-Host '  Manifest:       uiAccess="true" verifiziert' -ForegroundColor White
            Write-Host "  ShaderCache:    $copiedShaderCount / 9 O3-Shader (.cso)" -ForegroundColor White
            Write-Host '  Portable USB:   Unberuehrt' -ForegroundColor White
        } else {
            Write-Host '4/5 Erstelle bzw. pruefe lokales Code-Signing-Zertifikat...' -ForegroundColor Cyan
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

            Write-Host "5/5 Bereinige '$installDir', kopiere Dateien, signiere ZoomHost.exe und erstelle Verknuepfung..." -ForegroundColor Cyan
            if (Test-Path $installDir) {
                Remove-Item $installDir -Recurse -Force
            }
            New-Item -ItemType Directory -Path $installDir -Force | Out-Null
            Copy-Item -Path (Join-Path $stageDir '*') -Destination $installDir -Recurse -Force

            $exePath = Join-Path $installDir 'ZoomHost.exe'
            $sig = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert -HashAlgorithm SHA256
            if ($sig.Status -ne 'Valid') {
                throw "Signierung nicht gueltig: $($sig.Status) - $($sig.StatusMessage)"
            }

            $desktop = [Environment]::GetFolderPath('Desktop')
            $lnkPath = Join-Path $desktop $shortcutName
            $shell = New-Object -ComObject WScript.Shell
            $lnk = $shell.CreateShortcut($lnkPath)
            $lnk.TargetPath = $exePath
            $lnk.WorkingDirectory = $installDir
            $lnk.Description = 'ZoomHost mit UIAccess (Vektor-Rendering auch ueber Startmenue, Suche & Admin-Fenster)'
            $lnk.Save()

            Write-Host ''
            Write-Host 'Fertig! Installierte UIAccess-Version bereit (mit ShaderCache):' -ForegroundColor Green
            Write-Host "  Programm:       $exePath" -ForegroundColor White
            Write-Host "  ShaderCache:    $copiedShaderCount / 9 O3-Shader (.cso)" -ForegroundColor White
            Write-Host "  Verknuepfung:   $lnkPath" -ForegroundColor White
            Write-Host '  Portable USB:   Alle portablen Release-Builds blieben unveraendert.' -ForegroundColor White
        }
    }
} catch {
    Write-Host ''
    Write-Host "FEHLER: $($_.Exception.Message)" -ForegroundColor Red
    if ($PauseOnExit -and -not $NoPause) {
        Read-Host 'Druecke Enter zum Schliessen'
    }
    exit 1
}

if ($PauseOnExit -and -not $NoPause) {
    Write-Host ''
    Read-Host 'Druecke Enter zum Schliessen'
}
