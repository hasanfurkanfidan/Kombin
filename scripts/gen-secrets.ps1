<#
.SYNOPSIS
    Prints strong random values for docker/.env.prod, one KEY=value per line (never written to a file: redirect or paste them yourself).
.EXAMPLE
    scripts\gen-secrets.ps1
.NOTES
    Values are letters and digits only, so they are safe in a connection string and in a shell. The admin password hash is not random:
    make it from the founder's password with scripts\admin-hash.ps1.
#>
$ErrorActionPreference = 'Stop'

function New-Secret([int] $Length) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $buffer = New-Object byte[] 64
    $result = New-Object System.Text.StringBuilder
    try {
        while ($result.Length -lt $Length) {
            $random.GetBytes($buffer)
            foreach ($byte in $buffer) {
                # 248 is the largest multiple of 62 below 256: bytes from it up are skipped, so every character is equally likely.
                if ($byte -lt 248 -and $result.Length -lt $Length) { [void] $result.Append($alphabet[$byte % 62]) }
            }
        }
    }
    finally { $random.Dispose() }
    $result.ToString()
}

"# Generated $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')). Keep these secret."
"JWT_SIGNING_KEY=$(New-Secret 64)"
"INTERNAL_API_KEY=$(New-Secret 48)"
"VISION_INTERNAL_KEY=$(New-Secret 32)"
"OUTBOUND_IP_SALT=$(New-Secret 32)"
"POSTGRES_PASSWORD=$(New-Secret 32)"
"MINIO_ROOT_PASSWORD=$(New-Secret 32)"
"BACKUP_PASSPHRASE=$(New-Secret 48)"
