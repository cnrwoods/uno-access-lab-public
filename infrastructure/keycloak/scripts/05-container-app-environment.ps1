# Create the external Consumption environment on the lab subnet.
. "$PSScriptRoot/common.ps1"

$SubnetId = Invoke-Az @(
    'network', 'vnet', 'subnet', 'show',
    '--resource-group', $ResourceGroup,
    '--vnet-name', $VnetName,
    '--name', $ContainerAppsSubnet,
    '--query', 'id', '-o', 'tsv'
)

if (-not $SubnetId) {
    throw "Could not resolve Container Apps subnet ID."
}

Invoke-Az @(
    "containerapp", "env", "create",
    "--name", $ContainerAppsEnvironment,
    "--resource-group", $ResourceGroup,
    "--location", $Location,
    "--enable-workload-profiles", "true",
    "--infrastructure-subnet-resource-id", $SubnetId,
    "--internal-only", "false",
    "--public-network-access", "Enabled",
    "--logs-destination", "none",
    "--output", "none"
)

Write-Host "Container Apps environment created."
Write-Host "Verify: Public network access = Enabled, Virtual IP = External, Consumption profile only."
Write-Host "Persistent log collection is disabled; live log streaming remains available."
