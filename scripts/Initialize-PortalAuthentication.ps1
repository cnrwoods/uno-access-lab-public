[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/managementportal/ManagementPortal.csproj'
if (-not (Test-Path -LiteralPath $projectPath)) { throw 'ManagementPortal.csproj was not found.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK first.' }

Write-Output 'Enter the client secret from fingauth > Clients > fingerprint-management-plane > Credentials.'
$secureClientSecret = Read-Host 'Keycloak client secret' -AsSecureString
if ($secureClientSecret.Length -eq 0) { $secureClientSecret.Dispose(); throw 'The client secret cannot be empty.' }

try {
    $clientSecret = [System.Net.NetworkCredential]::new('', $secureClientSecret).Password
    $secretDocument = @{ 'Authentication:ClientSecret' = $clientSecret } | ConvertTo-Json -Compress
    # Send through stdin: the secret is never part of a command argument or file
    # in the repository. The user-secrets command prints only a success count.
    $secretDocument | & dotnet user-secrets set --project $projectPath
    if ($LASTEXITCODE -ne 0) { throw '.NET user secrets could not be saved.' }
}
finally {
    $secureClientSecret.Dispose()
    Remove-Variable clientSecret, secretDocument -ErrorAction SilentlyContinue
}

Write-Output 'Portal authentication configured in local .NET user secrets.'
Write-Output 'Next: dotnet dev-certs https --trust'
Write-Output 'Then: dotnet run --project src/managementportal'
Write-Output 'Open https://localhost:7080/ and click Log in.'
