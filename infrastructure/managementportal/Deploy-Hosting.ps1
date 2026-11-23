[CmdletBinding()]
param(
    [ValidateSet('ValidateOnly', 'WhatIf', 'Apply')]
    [string]$Mode = 'ValidateOnly',
    [string]$ConfigPath = '',
    [string]$SubscriptionId = '',
    [string]$ResourceGroupName = 'RG-fingauth.conorwoods.co.uk',
    [string]$Location = 'uksouth',
    [string]$AppServicePlanName = 'asp-fingauth-uks',
    [string]$WebAppName = 'app-fingauth-uks',
    [string]$StorageAccountName = '',
    [string]$FrontDoorProfileName = 'afd-fingauth',
    [string]$PublicHostname = 'fingauth.conorwoods.co.uk',
    [switch]$StorageOnly
)

$ErrorActionPreference = 'Stop'
$ownershipMarker = 'uno-access-lab-managementportal'
$templatePath = Join-Path $PSScriptRoot 'azuredeploy.json'
$configNames = @('SubscriptionId', 'ResourceGroupName', 'Location', 'AppServicePlanName',
    'WebAppName', 'StorageAccountName', 'FrontDoorProfileName', 'PublicHostname')

# Explicit command-line values take precedence over private config.
$explicitValues = @{}
foreach ($configName in $configNames) {
    if ($PSBoundParameters.ContainsKey($configName)) {
        $explicitValues[$configName] = $PSBoundParameters[$configName]
    }
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $candidateConfig = Join-Path $PSScriptRoot 'config.ps1'
    if (Test-Path -LiteralPath $candidateConfig -PathType Leaf) { $ConfigPath = $candidateConfig }
} elseif (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw 'The explicitly selected configuration file does not exist.'
}
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) { . $ConfigPath }
foreach ($configName in $explicitValues.Keys) {
    Set-Variable -Name $configName -Value $explicitValues[$configName] -Scope Script
}

$parsedSubscription = [guid]::Empty
if (-not [guid]::TryParse($SubscriptionId, [ref]$parsedSubscription)) {
    throw 'Set SubscriptionId explicitly or in the ignored config.ps1.'
}
if ($ResourceGroupName -notmatch '^[a-zA-Z0-9_.()-]{1,90}$' -or
    $Location -notmatch '^[a-z0-9]+$' -or
    $AppServicePlanName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,59}$' -or
    $WebAppName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{1,59}$' -or
    $FrontDoorProfileName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,259}$' -or
    $PublicHostname -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]{0,251}[a-zA-Z0-9]$') {
    throw 'One or more resource names, location or public hostname is invalid.'
}
if ($StorageAccountName -and $StorageAccountName -notmatch '^[a-z0-9]{3,24}$') {
    throw 'StorageAccountName must contain 3-24 lowercase letters/digits.'
}
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) { throw 'ARM template missing.' }

function Invoke-HostingAzJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $jsonLines = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        # Do not echo arbitrary arguments. This script never reads secret values.
        throw "Azure CLI command failed (exit $LASTEXITCODE). See the local CLI error."
    }
    if (-not $jsonLines) { return $null }
    return (($jsonLines -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Assert-HostingOwnership {
    param($Resource, [string]$Label)
    if ($null -ne $Resource -and
        ($null -eq $Resource.tags -or $Resource.tags.ManagedBy -ne $ownershipMarker -or
         $Resource.tags.Application -ne 'fingauth-managementportal')) {
        throw "$Label already exists without this deployment's ownership tags. No changes were applied."
    }
}

function Test-HostingLocation {
    param([string]$Actual, [string]$Expected)
    # Microsoft.Web GET can return display text (UK South) for the canonical
    # deployment location (uksouth). Whitespace/case do not change the region.
    return (($Actual -replace '\s', '') -ieq ($Expected -replace '\s', ''))
}

# Metadata only: never list app settings, publishing profiles or storage keys.
$group = Invoke-HostingAzJson @('group', 'show', '--name', $ResourceGroupName,
    '--subscription', $SubscriptionId)
if ($null -eq $group -or $group.name -ine $ResourceGroupName) { throw 'Expected existing resource group not found.' }
$resourceGroupId = $group.id
$inventory = @(Invoke-HostingAzJson @('resource', 'list', '--resource-group', $ResourceGroupName,
    '--subscription', $SubscriptionId))
$existingPlan = @($inventory | Where-Object {
    $_.type -ieq 'Microsoft.Web/serverfarms' -and $_.name -ieq $AppServicePlanName
})
$existingApp = @($inventory | Where-Object {
    $_.type -ieq 'Microsoft.Web/sites' -and $_.name -ieq $WebAppName
})
if ($existingPlan.Count -gt 1 -or $existingApp.Count -gt 1) { throw 'Ambiguous existing resource inventory.' }
$plan = if ($existingPlan.Count -eq 1) { $existingPlan[0] } else { $null }
$app = if ($existingApp.Count -eq 1) { $existingApp[0] } else { $null }
Assert-HostingOwnership $plan 'App Service plan'
Assert-HostingOwnership $app 'Web app'

if ($null -ne $plan) {
    $planDetails = Invoke-HostingAzJson @('resource', 'show', '--ids', $plan.id,
        '--api-version', '2024-11-01', '--subscription', $SubscriptionId)
    if (-not (Test-HostingLocation $planDetails.location $Location) -or -not $planDetails.properties.reserved -or
        $planDetails.sku.name -ine 'B1' -or $planDetails.sku.capacity -ne 1) {
        throw "Existing owned plan differs from Linux B1/one worker/location (location=$($planDetails.location), Linux=$($planDetails.properties.reserved), SKU=$($planDetails.sku.name), workers=$($planDetails.sku.capacity)). Refusing an implicit resize or move."
    }
}

$initializeAppSettings = $null -eq $app
if ($null -ne $app) {
    $appDetails = Invoke-HostingAzJson @('resource', 'show', '--ids', $app.id,
        '--api-version', '2024-11-01', '--subscription', $SubscriptionId)
    $expectedPlanId = "$resourceGroupId/providers/Microsoft.Web/serverfarms/$AppServicePlanName"
    if (-not (Test-HostingLocation $appDetails.location $Location) -or $appDetails.kind -notmatch 'linux' -or
        $appDetails.kind -match 'functionapp' -or
        $appDetails.properties.serverFarmId -ine $expectedPlanId) {
        throw 'Existing owned app differs from the requested Linux app/plan/location.'
    }
    if ($app.tags.PublicHostname -ine $PublicHostname -or
        [string]::IsNullOrWhiteSpace($app.tags.HostingStorageAccount)) {
        throw 'Existing app configuration identity is incompatible; review it without replacing settings.'
    }
    if (-not $StorageAccountName) {
        $StorageAccountName = [string]$app.tags.HostingStorageAccount
    } elseif ($StorageAccountName -ine $app.tags.HostingStorageAccount) {
        throw 'Changing the policy storage account requires a separate migration; app settings were not read or replaced.'
    }
    Write-Host 'Existing owned app: preserving all application settings, including later Key Vault references.'
}

$profile = Invoke-HostingAzJson @('afd', 'profile', 'show', '--resource-group', $ResourceGroupName,
    '--profile-name', $FrontDoorProfileName, '--subscription', $SubscriptionId)
$frontDoorId = [string]$profile.frontDoorId
$parsedFrontDoor = [guid]::Empty
if (-not [guid]::TryParse($frontDoorId, [ref]$parsedFrontDoor)) {
    throw 'Existing Front Door profile did not return a valid frontDoorId.'
}

$armParameters = @(
    "location=$Location",
    "appServicePlanName=$AppServicePlanName",
    "webAppName=$WebAppName",
    "publicHostname=$PublicHostname",
    "frontDoorId=$frontDoorId",
    "includeAppService=$(((-not $StorageOnly.IsPresent)).ToString().ToLowerInvariant())",
    "initializeAppSettings=$($initializeAppSettings.ToString().ToLowerInvariant())"
)
if ($StorageAccountName) { $armParameters += "storageAccountName=$StorageAccountName" }

# Resolve deterministic defaults using read-only ARM validation.
$validation = Invoke-HostingAzJson (@('deployment', 'group', 'validate',
    '--resource-group', $ResourceGroupName, '--subscription', $SubscriptionId,
    '--template-file', $templatePath, '--parameters') + $armParameters)
if (-not $StorageAccountName) {
    $StorageAccountName = [string]$validation.properties.parameters.storageAccountName.value
    if ($StorageAccountName -notmatch '^[a-z0-9]{3,24}$') {
        throw 'ARM validation did not resolve the default storage name; set StorageAccountName explicitly.'
    }
    $armParameters += "storageAccountName=$StorageAccountName"
}
$existingStorage = @($inventory | Where-Object {
    $_.type -ieq 'Microsoft.Storage/storageAccounts' -and $_.name -ieq $StorageAccountName
})
if ($existingStorage.Count -gt 1) { throw 'Ambiguous storage account inventory.' }
$storage = if ($existingStorage.Count -eq 1) { $existingStorage[0] } else { $null }
Assert-HostingOwnership $storage 'Storage account'
if ($null -ne $storage) {
    $storageDetails = Invoke-HostingAzJson @('resource', 'show', '--ids', $storage.id,
        '--api-version', '2023-05-01', '--subscription', $SubscriptionId)
    if (-not (Test-HostingLocation $storageDetails.location $Location) -or $storageDetails.kind -ine 'StorageV2' -or
        $storageDetails.sku.name -ine 'Standard_LRS') {
        throw 'Existing owned storage account differs from the requested region/kind/redundancy.'
    }
}
if ($null -eq $storage) {
    $availability = Invoke-HostingAzJson @('storage', 'account', 'check-name', '--name',
        $StorageAccountName, '--subscription', $SubscriptionId)
    if (-not $availability.nameAvailable) {
        throw 'Storage account name is unavailable outside the intended owned resource set.'
    }
}

if ($StorageOnly) {
    Write-Host "Validated storage only: $StorageAccountName/policies, $Location. App Service/identity remain pending."
} else {
    Write-Host "Validated foundation: $WebAppName, Linux B1 x1, $StorageAccountName/policies, $Location."
}
Write-Host 'No secrets, app package, policy data, AFD routes, public DNS or Keycloak settings are changed.'

if ($Mode -eq 'ValidateOnly') {
    [pscustomobject]@{
        mode = $Mode
        subscriptionId = $SubscriptionId
        resourceGroup = $ResourceGroupName
        location = $Location
        appServicePlanName = $AppServicePlanName
        webAppName = $WebAppName
        storageAccountName = $StorageAccountName
        frontDoorId = $frontDoorId
        initializeAppSettings = $initializeAppSettings
        armValidation = 'Succeeded'
        appServiceIncluded = (-not $StorageOnly.IsPresent)
    } | ConvertTo-Json
    return
}
if ($Mode -eq 'WhatIf') {
    $whatIfArguments = @('deployment', 'group', 'what-if', '--resource-group', $ResourceGroupName,
        '--subscription', $SubscriptionId, '--template-file', $templatePath, '--parameters') +
        $armParameters + @('--only-show-errors')
    & az @whatIfArguments
    if ($LASTEXITCODE -ne 0) { throw 'Azure deployment what-if failed.' }
    return
}

$deploymentName = if ($StorageOnly) { 'fingauth-managementportal-storage' } else { 'fingauth-managementportal-foundation' }
$deployment = Invoke-HostingAzJson (@('deployment', 'group', 'create', '--name', $deploymentName,
    '--resource-group', $ResourceGroupName, '--subscription', $SubscriptionId,
    '--mode', 'Incremental', '--template-file', $templatePath, '--parameters') + $armParameters)
if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'Hosting deployment did not finish successfully.' }

# Template outputs contain resource IDs, hostnames and principal IDs only.
$deployment.properties.outputs | ConvertTo-Json -Depth 6
