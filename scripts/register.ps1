<#
.SYNOPSIS
    Registra LumePDF per l'utente corrente come programma per aprire i PDF.

.DESCRIPTION
    Aggiunge LumePDF a "Apri con" e a Impostazioni > App > App predefinite, senza diritti di
    amministratore (scrive solo in HKEY_CURRENT_USER). Windows 11 non permette a un programma
    di impostarsi da solo come predefinito: alla fine si apre la pagina delle impostazioni,
    dove basta scegliere LumePDF per i file .pdf.

.PARAMETER Unregister
    Rimuove la registrazione.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\register.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\register.ps1 -Unregister
#>
param([switch]$Unregister)

$ErrorActionPreference = 'Stop'
$progId = 'LumePDF.PDF'
$exe = Join-Path $PSScriptRoot 'LumePDF.exe'
$classes = 'HKCU:\Software\Classes'

if ($Unregister) {
    Remove-Item "$classes\$progId" -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty "$classes\.pdf\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue
    Remove-Item "$classes\Applications\LumePDF.exe" -Recurse -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\LumePDF\Capabilities' -Recurse -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'LumePDF' -ErrorAction SilentlyContinue
    Write-Host 'Registrazione di LumePDF rimossa.'
    return
}

if (-not (Test-Path $exe)) { throw "LumePDF.exe non trovato in $PSScriptRoot" }

function Set-Default($path, $value) {
    New-Item $path -Force | Out-Null
    Set-ItemProperty $path -Name '(default)' -Value $value
}

# ProgID: how LumePDF opens a PDF
Set-Default "$classes\$progId" 'Documento PDF'
Set-Default "$classes\$progId\DefaultIcon" "`"$exe`",0"
Set-Default "$classes\$progId\shell\open\command" "`"$exe`" `"%1`""

# "Apri con" list
New-Item "$classes\.pdf\OpenWithProgids" -Force | Out-Null
New-ItemProperty "$classes\.pdf\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
New-Item "$classes\Applications\LumePDF.exe\SupportedTypes" -Force | Out-Null
Set-ItemProperty "$classes\Applications\LumePDF.exe" -Name 'FriendlyAppName' -Value 'LumePDF'
New-ItemProperty "$classes\Applications\LumePDF.exe\SupportedTypes" -Name '.pdf' -Value '' -PropertyType String -Force | Out-Null
Set-Default "$classes\Applications\LumePDF.exe\shell\open\command" "`"$exe`" `"%1`""

# Settings > Default apps
$caps = 'HKCU:\Software\LumePDF\Capabilities'
New-Item "$caps\FileAssociations" -Force | Out-Null
Set-ItemProperty $caps -Name 'ApplicationName' -Value 'LumePDF'
Set-ItemProperty $caps -Name 'ApplicationDescription' -Value 'Lettore PDF leggero'
Set-ItemProperty "$caps\FileAssociations" -Name '.pdf' -Value $progId
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'LumePDF' -Value 'Software\LumePDF\Capabilities'

# Tell Explorer that associations changed
Add-Type -Namespace LumePDF -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, System.IntPtr a, System.IntPtr b);'
[LumePDF.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "LumePDF registrato ($exe)."
Write-Host 'Ora scegli LumePDF come app predefinita per i file .pdf nella finestra che si apre.'
Start-Process 'ms-settings:defaultapps?registeredAppUser=LumePDF'
