# Simulate registration for one local UNO. Never prints the registration key.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dataDirectory = Join-Path $repoPath 'src\managementportal\App_Data'
$sketchDirectory = Join-Path $repoPath 'hardware\arduino\SEN0359\fingerprint_Matching_inos\fingerprint_and_pin_withRemotePolicy'
$keyPath = [System.IO.Path]::GetFullPath((Join-Path $dataDirectory 'device-shared-key.hex'))
$headerPath = [System.IO.Path]::GetFullPath((Join-Path $sketchDirectory 'arduino_secrets.h'))
$repoPrefix = $repoPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
foreach ($targetPath in @($keyPath, $headerPath)) {
    if (-not $targetPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The setup target must be inside this repository.'
    }
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $sketchDirectory -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (Test-Path -LiteralPath $keyPath) {
    $keyHex = [System.IO.File]::ReadAllText($keyPath).Trim().ToLowerInvariant()
    if ($keyHex -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The existing registration-key file is invalid. It has not been changed.'
    }
}
else {
    # Never silently create a different server key for an already provisioned device.
    if (Test-Path -LiteralPath $headerPath) {
        throw 'A sketch secrets file already exists but the server key is missing. Restore the matching server key before running setup.'
    }
    $keyBytes = New-Object byte[] 32
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($keyBytes) }
    finally { $random.Dispose() }
    $keyHex = -join ($keyBytes | ForEach-Object { $_.ToString('x2') })
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    $keyStream = [System.IO.File]::Open($keyPath, [System.IO.FileMode]::CreateNew)
    try {
        $keyText = $utf8.GetBytes($keyHex)
        $keyStream.Write($keyText, 0, $keyText.Length)
        [Array]::Clear($keyText, 0, $keyText.Length)
    }
    finally { $keyStream.Dispose() }
}

if (Test-Path -LiteralPath $headerPath) {
    $existingHeader = [System.IO.File]::ReadAllText($headerPath)
    if (-not $existingHeader.Contains($keyHex)) {
        throw 'The sketch secrets file does not contain the matching registration key. It has not been overwritten.'
    }
    Write-Output 'Existing registration key and sketch secrets retained.'
}
else {
    $header = @"
#pragma once
// Local registration and Wi-Fi settings. This file is ignored by Git.
#define SECRET_SSID ""
#define SECRET_PASS ""
#define POLICY_HOST "192.168.1.100"
#define POLICY_PORT 5080
#define SHARED_KEY_HEX "$keyHex"
"@
    $headerStream = [System.IO.File]::Open($headerPath, [System.IO.FileMode]::CreateNew)
    try {
        $headerBytes = $utf8.GetBytes($header)
        $headerStream.Write($headerBytes, 0, $headerBytes.Length)
        [Array]::Clear($headerBytes, 0, $headerBytes.Length)
    }
    finally { $headerStream.Dispose() }
    Write-Output 'Created matching local registration files for the portal and UNO.'
}

Write-Output ('Portal key file: ' + $keyPath)
Write-Output ('Set Wi-Fi credentials and the computer LAN address in: ' + $headerPath)
Write-Output 'Restart the portal after provisioning. Both files are ignored by Git; keep them out of screenshots and posts.'
