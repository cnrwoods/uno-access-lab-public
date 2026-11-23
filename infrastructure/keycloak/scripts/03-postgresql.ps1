# Provision private PostgreSQL and save database/bootstrap credentials in Key Vault.
param([switch]$ResumeExistingServer)
. "$PSScriptRoot/common.ps1"

$PrivateDnsZone = "fingauth.private.postgres.database.azure.com"

if (-not $ResumeExistingServer) {
    Invoke-Az @(
        "network", "private-dns", "zone", "create",
        "--resource-group", $ResourceGroup,
        "--name", $PrivateDnsZone,
        "--subscription", $Subscription,
        "--output", "none"
    )

    Invoke-Az @(
        "network", "private-dns", "link", "vnet", "create",
        "--resource-group", $ResourceGroup,
        "--zone-name", $PrivateDnsZone,
        "--name", "link-fingauth-postgres",
        "--virtual-network", $VnetName,
        "--registration-enabled", "false",
        "--subscription", $Subscription,
        "--output", "none"
    )
}
else {
    $ServerJson = Invoke-Az @('postgres', 'flexible-server', 'show',
        '--resource-group', $ResourceGroup, '--name', $PostgresServerName,
        '--subscription', $Subscription, '-o', 'json')
    $Server = ConvertFrom-Json -InputObject ($ServerJson -join "`n")
    if ($Server.state -ne 'Ready' -or $Server.administratorLogin -ne $PostgresAdminUser) {
        throw 'Existing PostgreSQL server must be Ready and its administrator must match PostgresAdminUser in config.'
    }
    Write-Host "Resuming with existing server '$PostgresServerName'; its password will not be reset."
}

$PasswordPrompt = if ($ResumeExistingServer) { 'Enter the SAME PostgreSQL administrator password used when creating this server' } else { 'Enter a strong PostgreSQL administrator password' }
$DbPasswordSecure = Read-Host $PasswordPrompt -AsSecureString
$DbPassword = Get-PlaintextFromSecureString $DbPasswordSecure

try {
    if (-not $ResumeExistingServer) {
        Invoke-Az @(
            "postgres", "flexible-server", "create",
            "--resource-group", $ResourceGroup,
            "--name", $PostgresServerName,
            "--location", $Location,
            "--admin-user", $PostgresAdminUser,
            "--admin-password", $DbPassword,
            "--sku-name", "Standard_B1ms",
            "--tier", "Burstable",
            "--version", "17",
            "--storage-size", "32",
            "--storage-auto-grow", "Enabled",
            "--backup-retention", "7",
            # HA is disabled by default; Burstable does not support HA.
            "--geo-redundant-backup", "Disabled",
            "--vnet", $VnetName,
            "--subnet", $PostgresSubnet,
            "--private-dns-zone", $PrivateDnsZone,
            "--subscription", $Subscription,
            "--output", "none"
        )
    }

    Write-Host "Creating database '$PostgresDatabase' inside server '$PostgresServerName'..."
    Invoke-Az @(
        "postgres", "flexible-server", "db", "create",
        "--resource-group", $ResourceGroup,
        "--server-name", $PostgresServerName,
        "--name", $PostgresDatabase,
        "--subscription", $Subscription,
        "--output", "none"
    )

    Write-Host "Writing versionless Key Vault-backed database credentials..."
    Invoke-Az @(
        "keyvault", "secret", "set",
        "--vault-name", $KeyVaultName,
        "--name", "kc-db-username",
        "--value", $PostgresAdminUser,
        "--subscription", $Subscription,
        "--output", "none"
    )

    Invoke-Az @(
        "keyvault", "secret", "set",
        "--vault-name", $KeyVaultName,
        "--name", "kc-db-password",
        "--value", $DbPassword,
        "--subscription", $Subscription,
        "--output", "none"
    )
}
finally {
    $DbPassword = $null
}

$BootstrapUser = Read-Host "Enter temporary Keycloak bootstrap administrator username (recommended: kc-bootstrap)"
if ([string]::IsNullOrWhiteSpace($BootstrapUser)) {
    $BootstrapUser = "kc-bootstrap"
}

$BootstrapPasswordSecure = Read-Host "Enter a strong temporary Keycloak bootstrap administrator password" -AsSecureString
$BootstrapPassword = Get-PlaintextFromSecureString $BootstrapPasswordSecure

try {
    Invoke-Az @(
        "keyvault", "secret", "set",
        "--vault-name", $KeyVaultName,
        "--name", "kc-bootstrap-user",
        "--value", $BootstrapUser,
        "--subscription", $Subscription,
        "--output", "none"
    )

    Invoke-Az @(
        "keyvault", "secret", "set",
        "--vault-name", $KeyVaultName,
        "--name", "kc-bootstrap-pass",
        "--value", $BootstrapPassword,
        "--subscription", $Subscription,
        "--output", "none"
    )
}
finally {
    $BootstrapPassword = $null
}

Write-Host "PostgreSQL and bootstrap Key Vault secrets created."
