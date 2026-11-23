# Create the public hostname, or associate it with the route after DNS validation.
param([switch]$Associate)
. "$PSScriptRoot/common.ps1"

if (-not $Associate) {
    Invoke-Az @('afd', 'custom-domain', 'create', '--resource-group', $ResourceGroup,
        '--profile-name', $FrontDoorProfile, '--custom-domain-name', $CustomDomainName,
        '--host-name', $PublicHostname, '--certificate-type', 'ManagedCertificate',
        '--minimum-tls-version', 'TLS12', '--output', 'none')
}
$domainJson = Invoke-Az @('afd', 'custom-domain', 'show', '--resource-group', $ResourceGroup,
    '--profile-name', $FrontDoorProfile, '--custom-domain-name', $CustomDomainName, '-o', 'json')
$domain = $domainJson | ConvertFrom-Json
if ($Associate) {
    if ($domain.domainValidationState -ne 'Approved') { throw 'DNS validation is not Approved yet. Wait and rerun with -Associate.' }
    $tempFile = Join-Path ([IO.Path]::GetTempPath()) "afd-domain-$([guid]::NewGuid()).json"
    try {
        ConvertTo-Json -InputObject @(@{ id = $domain.id }) | Set-Content -LiteralPath $tempFile -Encoding utf8
        Invoke-Az @('afd', 'route', 'update', '--resource-group', $ResourceGroup,
            '--profile-name', $FrontDoorProfile, '--endpoint-name', $FrontDoorEndpoint,
            '--route-name', $FrontDoorRoute, '--formatted-custom-domains', "@$tempFile", '--output', 'none')
    }
    finally { Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue }
    Write-Host 'Custom domain associated. Wait for certificate and edge deployment before testing HTTPS.'
    return
}
$afdHost = Invoke-Az @('afd', 'endpoint', 'show', '--resource-group', $ResourceGroup,
    '--profile-name', $FrontDoorProfile, '--endpoint-name', $FrontDoorEndpoint, '--query', 'hostName', '-o', 'tsv')
Write-Host "TXT   _dnsauth.$PublicHostname   $($domain.validationProperties.validationToken)"
Write-Host "CNAME $PublicHostname            $afdHost"
Write-Host 'After validation is Approved: .\scripts\09-custom-domain.ps1 -Associate'
