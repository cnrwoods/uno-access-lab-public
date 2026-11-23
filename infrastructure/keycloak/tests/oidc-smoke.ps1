. "$PSScriptRoot/../scripts/common.ps1"

$ErrorActionPreference = "Stop"

$MasterDiscovery = "https://$PublicHostname/realms/master/.well-known/openid-configuration"
$RealmDiscovery = "https://$PublicHostname/realms/$RealmName/.well-known/openid-configuration"

Write-Host "Testing master discovery endpoint..."
$master = Invoke-RestMethod -Uri $MasterDiscovery -Method Get
if (-not $master.issuer) { throw "Master realm discovery response did not contain issuer." }
Write-Host "PASS: $($master.issuer)"

Write-Host "Testing fingauth discovery endpoint..."
$realm = Invoke-RestMethod -Uri $RealmDiscovery -Method Get
if (-not $realm.issuer) { throw "Realm discovery response did not contain issuer." }
Write-Host "PASS: $($realm.issuer)"

$ExpectedIssuer = "https://$PublicHostname/realms/$RealmName"
if ($realm.issuer -ne $ExpectedIssuer) {
    throw "Issuer mismatch. Expected '$ExpectedIssuer' but received '$($realm.issuer)'."
}

Write-Host "PASS: issuer matches expected public hostname."
