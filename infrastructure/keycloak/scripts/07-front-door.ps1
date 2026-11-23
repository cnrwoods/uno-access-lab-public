# Create Front Door routing to the Container Apps origin over HTTPS.
. "$PSScriptRoot/common.ps1"

$AcaFqdn = Invoke-Az @(
    'containerapp', 'show',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--query', 'properties.configuration.ingress.fqdn', '-o', 'tsv'
)

if (-not $AcaFqdn) {
    throw "Could not resolve Container App FQDN."
}

Invoke-Az @(
    "afd", "profile", "create",
    "--profile-name", $FrontDoorProfile,
    "--resource-group", $ResourceGroup,
    "--sku", "Standard_AzureFrontDoor",
    "--output", "none"
)

Invoke-Az @(
    "afd", "endpoint", "create",
    "--resource-group", $ResourceGroup,
    "--endpoint-name", $FrontDoorEndpoint,
    "--profile-name", $FrontDoorProfile,
    "--enabled-state", "Enabled",
    "--output", "none"
)

Invoke-Az @(
    "afd", "origin-group", "create",
    "--resource-group", $ResourceGroup,
    "--origin-group-name", $FrontDoorOriginGroup,
    "--profile-name", $FrontDoorProfile,
    "--probe-request-type", "GET",
    "--probe-protocol", "Https",
    "--probe-interval-in-seconds", "60",
    "--probe-path", "/realms/master/.well-known/openid-configuration",
    "--sample-size", "4",
    "--successful-samples-required", "3",
    "--additional-latency-in-milliseconds", "50",
    "--output", "none"
)

Invoke-Az @(
    "afd", "origin", "create",
    "--resource-group", $ResourceGroup,
    "--origin-group-name", $FrontDoorOriginGroup,
    "--origin-name", $FrontDoorOrigin,
    "--profile-name", $FrontDoorProfile,
    "--host-name", $AcaFqdn,
    "--origin-host-header", $AcaFqdn,
    "--priority", "1",
    "--weight", "500",
    "--https-port", "443",
    "--enforce-certificate-name-check", "true",
    "--enabled-state", "Enabled",
    "--output", "none"
)

Invoke-Az @(
    "afd", "route", "create",
    "--resource-group", $ResourceGroup,
    "--profile-name", $FrontDoorProfile,
    "--endpoint-name", $FrontDoorEndpoint,
    "--route-name", $FrontDoorRoute,
    "--origin-group", $FrontDoorOriginGroup,
    "--supported-protocols", "[Http,Https]",
    "--patterns-to-match", "[/*]",
    "--https-redirect", "Enabled",
    "--forwarding-protocol", "HttpsOnly",
    "--link-to-default-domain", "Enabled",
    "--output", "none"
)

$AfdHost = Invoke-Az @(
    'afd', 'endpoint', 'show',
    '--resource-group', $ResourceGroup,
    '--profile-name', $FrontDoorProfile,
    '--endpoint-name', $FrontDoorEndpoint,
    '--query', 'hostName', '-o', 'tsv'
)

Write-Host ""
Write-Host "Front Door created: https://$AfdHost"
Write-Host "Next: 08-origin-hardening.ps1, then 09-custom-domain.ps1."
