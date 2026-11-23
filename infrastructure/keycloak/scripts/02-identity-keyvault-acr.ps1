# Create or reuse the managed identity, Key Vault, ACR and role assignments.
. "$PSScriptRoot/common.ps1"

if ($AcrName -cnotmatch '^[a-z0-9]{5,50}$') {
    throw 'Invalid AcrName in config/config.ps1. Use 5-50 lowercase letters or digits, without dashes (for example acrfingauth). The name must also be globally unique.'
}
if ($KeyVaultName -notmatch '^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$' -or $KeyVaultName -match '--') {
    throw 'Invalid KeyVaultName in config/config.ps1. Use 3-24 letters/digits/hyphens, start with a letter, end with a letter/digit, and avoid consecutive hyphens.'
}
if ($ManagedIdentityName -notmatch '^[a-zA-Z0-9-]+$') {
    throw 'Set ManagedIdentityName in config/config.ps1 using letters, digits or hyphens.'
}
if ([string]::IsNullOrWhiteSpace($Subscription) -or $Subscription -eq 'CHANGE_ME') {
    throw 'Set Subscription in config/config.ps1 before running step 02.'
}

# Scope resource requests to the configured subscription, independent of CLI defaults.
function Invoke-Step02Az {
    param([string[]]$Arguments)
    Invoke-Az ($Arguments + @('--subscription', $Subscription))
}

function Find-Step02Resource {
    param([string[]]$ListCommand, [string]$Name)
    # Filter in the CLI. Do not depend on PowerShell JSON-array enumeration,
    # which differs between Windows PowerShell and PowerShell 7.
    if ($Name -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid resource name in local config.' }
    $result = Invoke-Step02Az ($ListCommand + @('--resource-group', $ResourceGroup,
        '--query', "[?name=='$Name'].id | [0]", '--output', 'tsv'))
    return ($result -join "`n").Trim()
}

function Ensure-Step02Role {
    param([string]$PrincipalId, [string]$PrincipalType, [string]$Role, [string]$Scope)
    if (-not $PrincipalId -or -not $Scope) { throw "Missing principal or scope for $Role." }
    $existing = Invoke-Step02Az @('role', 'assignment', 'list', '--scope', $Scope,
        '--query', "[?principalId=='$PrincipalId' && roleDefinitionName=='$Role'].id | [0]", '-o', 'tsv')
    if (($existing -join '').Trim()) {
        Write-Host "Reusing $Role assignment for $PrincipalType."
        return
    }
    Write-Host "Granting $Role to $PrincipalType..."
    Invoke-Step02Az @('role', 'assignment', 'create', '--assignee-object-id', $PrincipalId,
        '--assignee-principal-type', $PrincipalType, '--role', $Role, '--scope', $Scope, '--output', 'none')
}

# Resolve the operator before creating anything. Graph does not accept --subscription.
$SignedInUserId = Invoke-Az @('ad', 'signed-in-user', 'show', '--query', 'id', '-o', 'tsv')
if (-not $SignedInUserId) { throw 'Sign in as the deployment user to grant Key Vault Secrets Officer.' }

$IdentityId = Find-Step02Resource @('identity', 'list') $ManagedIdentityName
if (-not $IdentityId) {
    Write-Host "Creating managed identity '$ManagedIdentityName'..."
    Invoke-Step02Az @('identity', 'create', '--name', $ManagedIdentityName,
        '--resource-group', $ResourceGroup, '--location', $Location, '--output', 'none')
}
else { Write-Host "Reusing managed identity '$ManagedIdentityName'." }
$IdentityJson = Invoke-Step02Az @('identity', 'show', '--name', $ManagedIdentityName,
    '--resource-group', $ResourceGroup, '-o', 'json')
$Identity = ConvertFrom-Json -InputObject ($IdentityJson -join "`n")
$IdentityId = $Identity.id
$IdentityPrincipalId = $Identity.principalId
if (-not $IdentityId -or -not $IdentityPrincipalId) { throw 'Could not resolve managed identity.' }

$KeyVaultId = Find-Step02Resource @('keyvault', 'list') $KeyVaultName
if (-not $KeyVaultId) {
    Write-Host "Creating Key Vault '$KeyVaultName'..."
    Invoke-Step02Az @('keyvault', 'create', '--name', $KeyVaultName,
        '--resource-group', $ResourceGroup, '--location', $Location,
        '--enable-rbac-authorization', 'true', '--enable-purge-protection', 'true',
        '--retention-days', '90', '--output', 'none')
}
else { Write-Host "Reusing Key Vault '$KeyVaultName' in '$ResourceGroup'." }
$VaultJson = Invoke-Step02Az @('keyvault', 'show', '--name', $KeyVaultName,
    '--resource-group', $ResourceGroup, '-o', 'json')
$Vault = ConvertFrom-Json -InputObject ($VaultJson -join "`n")
if ($Vault.properties.enableRbacAuthorization -ne $true -or $Vault.properties.enablePurgeProtection -ne $true) {
    throw 'Existing Key Vault must have RBAC authorization and purge protection enabled. Review its settings before continuing; the script has not changed its access model.'
}
$KeyVaultId = $Vault.id
if (-not $KeyVaultId) { throw 'Could not resolve the Key Vault resource ID.' }
Ensure-Step02Role $IdentityPrincipalId 'ServicePrincipal' 'Key Vault Secrets User' $KeyVaultId

Ensure-Step02Role $SignedInUserId 'User' 'Key Vault Secrets Officer' $KeyVaultId

$AcrId = Find-Step02Resource @('acr', 'list') $AcrName
if (-not $AcrId) {
    Write-Host "Creating Azure Container Registry '$AcrName' (Basic)..."
    Invoke-Step02Az @('acr', 'create', '--resource-group', $ResourceGroup,
        '--name', $AcrName, '--location', $Location, '--sku', 'Basic',
        '--admin-enabled', 'false', '--output', 'none')
}
else { Write-Host "Reusing Azure Container Registry '$AcrName'." }
$AcrJson = Invoke-Step02Az @('acr', 'show', '--name', $AcrName,
    '--resource-group', $ResourceGroup, '-o', 'json')
$Registry = ConvertFrom-Json -InputObject ($AcrJson -join "`n")
if (-not $Registry.id) { throw 'Could not resolve registry resource ID.' }
if ($Registry.adminUserEnabled) { throw 'Registry admin credentials are enabled. Disable them before continuing with this managed-identity deployment.' }
if ($Registry.sku.name -ne 'Basic') { Write-Warning 'Existing registry is not Basic; its SKU has been preserved. Review its cost.' }
Ensure-Step02Role $IdentityPrincipalId 'ServicePrincipal' 'AcrPull' $Registry.id

Write-Host 'Step 02 complete: identity, protected Key Vault, registry and all three role assignments are ready.'
