# Remove bootstrap configuration after the permanent administrator has been tested.
. "$PSScriptRoot/common.ps1"

Write-Warning "Run this only after a permanent Keycloak administrator exists and has been tested."

$confirmation = Read-Host "Type REMOVE to remove bootstrap environment variables and delete the Key Vault secrets"
if ($confirmation -ne "REMOVE") {
    Write-Host "Cancelled."
    exit 0
}

# Remove bootstrap variables from the Container App.
Invoke-Az @(
    'containerapp', 'update',
    '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup,
    '--remove-env-vars', 'KC_BOOTSTRAP_ADMIN_USERNAME', 'KC_BOOTSTRAP_ADMIN_PASSWORD',
    '--output', 'none'
)

# Remove the now-unused ACA secret references before deleting their backing secrets.
Invoke-Az @('containerapp', 'secret', 'remove', '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup, '--secret-names', 'kc-admin-user', 'kc-admin-pass', '--output', 'none')
Invoke-Az @(
    "keyvault", "secret", "delete",
    "--vault-name", $KeyVaultName,
    "--name", "kc-bootstrap-user",
    "--output", "none"
)

Invoke-Az @(
    "keyvault", "secret", "delete",
    "--vault-name", $KeyVaultName,
    "--name", "kc-bootstrap-pass",
    "--output", "none"
)

Write-Host "Bootstrap credentials removed from the active Container App configuration and deleted from Key Vault."
Write-Host "Because purge protection is enabled, deleted secrets remain recoverable for the configured retention period."
