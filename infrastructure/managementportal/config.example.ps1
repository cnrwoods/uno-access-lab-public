# Copy to config.ps1 (Git-ignored), or pass these named parameters directly.
# Resource names/IDs only. Never put credentials, storage keys or secret values here.
$SubscriptionId = 'CHANGE_ME'
$ResourceGroupName = 'RG-fingauth.conorwoods.co.uk'
$Location = 'uksouth'
$AppServicePlanName = 'asp-fingauth-uks'
$WebAppName = 'app-fingauth-uks'
# Optional. Empty uses the template's stfingauth + uniqueString(resourceGroup.id).
$StorageAccountName = ''
$FrontDoorProfileName = 'afd-fingauth'
$PublicHostname = 'fingauth.conorwoods.co.uk'
