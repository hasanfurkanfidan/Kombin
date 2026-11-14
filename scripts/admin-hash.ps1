# Prints the password hash for Admin:Users (ADMIN_PASSWORD_HASH in docker/.env).
#   scripts\admin-hash.ps1             asks for the password (no echo)
# The password must be at least 12 characters. It is never printed or stored.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$secure = Read-Host -Prompt 'Password' -AsSecureString
$password = [System.Net.NetworkCredential]::new('', $secure).Password

# Through standard input, so the password does not show up in the process list.
$password | dotnet run --project src/Kombin.Api --verbosity quiet -- hash-password
