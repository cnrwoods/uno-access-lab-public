# Create the lab resource group, VNet and delegated subnets.
. "$PSScriptRoot/common.ps1"

Invoke-Az @(
    "group", "create",
    "--name", $ResourceGroup,
    "--location", $Location,
    "--output", "none"
)

Invoke-Az @(
    "network", "vnet", "create",
    "--resource-group", $ResourceGroup,
    "--name", $VnetName,
    "--location", $Location,
    "--address-prefixes", $VnetCidr,
    "--output", "none"
)

Invoke-Az @(
    "network", "vnet", "subnet", "create",
    "--resource-group", $ResourceGroup,
    "--vnet-name", $VnetName,
    "--name", $ContainerAppsSubnet,
    "--address-prefixes", $ContainerAppsSubnetCidr,
    "--delegations", "Microsoft.App/environments",
    "--output", "none"
)

Invoke-Az @(
    "network", "vnet", "subnet", "create",
    "--resource-group", $ResourceGroup,
    "--vnet-name", $VnetName,
    "--name", $PrivateEndpointSubnet,
    "--address-prefixes", $PrivateEndpointSubnetCidr,
    "--output", "none"
)

Invoke-Az @(
    "network", "vnet", "subnet", "create",
    "--resource-group", $ResourceGroup,
    "--vnet-name", $VnetName,
    "--name", $PostgresSubnet,
    "--address-prefixes", $PostgresSubnetCidr,
    "--delegations", "Microsoft.DBforPostgreSQL/flexibleServers",
    "--output", "none"
)

Write-Host "Foundation networking created."
