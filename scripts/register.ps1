<#
.SYNOPSIS
    Registra Lieve per l'utente corrente come programma per aprire i PDF.

.DESCRIPTION
    Aggiunge Lieve a "Apri con" e a Impostazioni > App > App predefinite, senza diritti di
    amministratore (scrive solo in HKEY_CURRENT_USER). Windows 11 non permette a un programma
    di impostarsi da solo come predefinito: alla fine si apre la pagina delle impostazioni,
    dove basta scegliere Lieve per i file .pdf.

.PARAMETER Unregister
    Rimuove la registrazione.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\register.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\register.ps1 -Unregister
#>
param([switch]$Unregister)

$ErrorActionPreference = 'Stop'
$progId = 'Lieve.PDF'
$exe = Join-Path $PSScriptRoot 'Lieve.exe'
$classes = 'HKCU:\Software\Classes'

if ($Unregister) {
    Remove-Item "$classes\$progId" -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty "$classes\.pdf\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue
    Remove-Item "$classes\Applications\Lieve.exe" -Recurse -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\Lieve\Capabilities' -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'Lieve' -ErrorAction SilentlyContinue
    Write-Host 'Registrazione di Lieve rimossa.'
    return
}

if (-not (Test-Path $exe)) { throw "Lieve.exe non trovato in $PSScriptRoot" }

function Set-Default($path, $value) {
    New-Item $path -Force | Out-Null
    Set-ItemProperty $path -Name '(default)' -Value $value
}

# ProgID: how Lieve opens a PDF
Set-Default "$classes\$progId" 'Documento PDF'
Set-Default "$classes\$progId\DefaultIcon" "`"$exe`",0"
Set-Default "$classes\$progId\shell\open\command" "`"$exe`" `"%1`""

# "Apri con" list
New-Item "$classes\.pdf\OpenWithProgids" -Force | Out-Null
New-ItemProperty "$classes\.pdf\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
New-Item "$classes\Applications\Lieve.exe\SupportedTypes" -Force | Out-Null
Set-ItemProperty "$classes\Applications\Lieve.exe" -Name 'FriendlyAppName' -Value 'Lieve'
New-ItemProperty "$classes\Applications\Lieve.exe\SupportedTypes" -Name '.pdf' -Value '' -PropertyType String -Force | Out-Null
Set-Default "$classes\Applications\Lieve.exe\shell\open\command" "`"$exe`" `"%1`""

# Settings > Default apps
$caps = 'HKCU:\Software\Lieve\Capabilities'
New-Item "$caps\FileAssociations" -Force | Out-Null
Set-ItemProperty $caps -Name 'ApplicationName' -Value 'Lieve'
Set-ItemProperty $caps -Name 'ApplicationDescription' -Value 'Lettore PDF leggero'
Set-ItemProperty "$caps\FileAssociations" -Name '.pdf' -Value $progId
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'Lieve' -Value 'Software\Lieve\Capabilities'

# Tell Explorer that associations changed
Add-Type -Namespace Lieve -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, System.IntPtr a, System.IntPtr b);'
[Lieve.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "Lieve registrato ($exe)."
Write-Host 'Ora scegli Lieve come app predefinita per i file .pdf nella finestra che si apre.'
Start-Process 'ms-settings:defaultapps?registeredAppUser=Lieve'
