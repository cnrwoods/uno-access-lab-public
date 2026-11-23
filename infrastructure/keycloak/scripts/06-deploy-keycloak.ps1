# Deploy the initial Keycloak configuration and wait for a healthy revision.
. "$PSScriptRoot/common.ps1"

function Wait-KeycloakRevision {
    param([string]$RevisionName, [int]$TimeoutSeconds = 660)
    if ([string]::IsNullOrWhiteSpace($RevisionName)) { throw 'Azure did not return a deployed revision name.' }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $json = Invoke-Az @('containerapp', 'revision', 'show', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--revision', $RevisionName, '--output', 'json')
        $revision = ConvertFrom-Json -InputObject ($json -join "`n")
        $status = $revision.properties
        if ($status.healthState -eq 'Healthy' -and $status.runningState -in @('Running', 'RunningAtMaxScale')) {
            Write-Host "Revision '$RevisionName' is Healthy and Running."
            return
        }
        if ($status.provisioningState -in @('Failed', 'Deprovisioned') -or
            $status.runningState -in @('Failed', 'Degraded', 'Stopped')) {
            throw "Keycloak revision '$RevisionName' failed: $($status.runningStateDetails). Inspect Container Apps console/system logs before retrying."
        }
        Write-Host "Waiting for Keycloak startup: health=$($status.healthState), running=$($status.runningState)."
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Seconds 15
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Keycloak revision '$RevisionName' did not become healthy within $TimeoutSeconds seconds. The resource exists, but startup is not confirmed. Inspect Container Apps console/system logs."
}

Write-Host 'Resolving managed identity, environment and Front Door backend ranges...'
$IdentityId = Invoke-Az @('identity', 'show', '--name', $ManagedIdentityName, '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--query', 'id', '-o', 'tsv')
$EnvironmentId = Invoke-Az @('containerapp', 'env', 'show', '--name', $ContainerAppsEnvironment, '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--query', 'id', '-o', 'tsv')
if (-not $IdentityId -or -not $EnvironmentId) { throw 'Identity or environment not found.' }
$restrictions = @(Get-FrontDoorRestrictions)
$identities = @{}
$identities[$IdentityId] = @{}
$vaultBase = "https://$KeyVaultName.vault.azure.net/secrets"
$secrets = @(
    @{ name = 'kc-db-user'; keyVaultUrl = "$vaultBase/kc-db-username"; identity = $IdentityId }
    @{ name = 'kc-db-pass'; keyVaultUrl = "$vaultBase/kc-db-password"; identity = $IdentityId }
    @{ name = 'kc-admin-user'; keyVaultUrl = "$vaultBase/kc-bootstrap-user"; identity = $IdentityId }
    @{ name = 'kc-admin-pass'; keyVaultUrl = "$vaultBase/kc-bootstrap-pass"; identity = $IdentityId }
)
$envVars = @(
    @{ name = 'KC_DB'; value = 'postgres' }
    @{ name = 'KC_DB_URL'; value = "jdbc:postgresql://$PostgresServerName.postgres.database.azure.com:5432/${PostgresDatabase}?sslmode=require" }
    @{ name = 'KC_DB_USERNAME'; secretRef = 'kc-db-user' }
    @{ name = 'KC_DB_PASSWORD'; secretRef = 'kc-db-pass' }
    @{ name = 'KC_HOSTNAME'; value = "https://$PublicHostname" }
    @{ name = 'KC_HTTP_ENABLED'; value = 'true' }
    @{ name = 'KC_PROXY_HEADERS'; value = 'xforwarded' }
    @{ name = 'KC_HEALTH_ENABLED'; value = 'true' }
    @{ name = 'KC_METRICS_ENABLED'; value = 'true' }
    @{ name = 'KC_HTTP_MANAGEMENT_PORT'; value = '9000' }
    @{ name = 'KC_HTTP_MANAGEMENT_SCHEME'; value = 'http' }
    @{ name = 'KC_DB_POOL_INITIAL_SIZE'; value = '5' }
    @{ name = 'KC_DB_POOL_MIN_SIZE'; value = '5' }
    @{ name = 'KC_DB_POOL_MAX_SIZE'; value = '10' }
    @{ name = 'KC_BOOTSTRAP_ADMIN_USERNAME'; secretRef = 'kc-admin-user' }
    @{ name = 'KC_BOOTSTRAP_ADMIN_PASSWORD'; secretRef = 'kc-admin-pass' }
)
$probes = @(
    @{ type = 'Startup'; httpGet = @{ path = '/health/started'; port = 9000; scheme = 'HTTP' }; initialDelaySeconds = 10; periodSeconds = 10; timeoutSeconds = 5; failureThreshold = 60 }
    @{ type = 'Readiness'; httpGet = @{ path = '/health/ready'; port = 9000; scheme = 'HTTP' }; periodSeconds = 10; timeoutSeconds = 5; failureThreshold = 3 }
    @{ type = 'Liveness'; httpGet = @{ path = '/health/live'; port = 9000; scheme = 'HTTP' }; periodSeconds = 30; timeoutSeconds = 5; failureThreshold = 3 }
)
# One complete configuration avoids creating a broken revision without DB secrets
# or a briefly unrestricted public endpoint. JSON is valid YAML for --yaml.
$app = @{
    name = $ContainerAppName
    location = $Location
    identity = @{ type = 'UserAssigned'; userAssignedIdentities = $identities }
    properties = @{
        environmentId = $EnvironmentId
        workloadProfileName = 'Consumption'
        configuration = @{
            activeRevisionsMode = 'Single'
            secrets = $secrets
            registries = @(@{ server = "$AcrName.azurecr.io"; identity = $IdentityId })
            ingress = @{ external = $true; targetPort = 8080; transport = 'auto'; allowInsecure = $false; ipSecurityRestrictions = $restrictions }
        }
        template = @{
            containers = @(@{
                name = 'keycloak'; image = "$AcrName.azurecr.io/${KeycloakImageName}:$KeycloakVersion"
                command = @('/opt/keycloak/bin/kc.sh'); args = @('start', '--optimized')
                resources = @{ cpu = $KeycloakCpu; memory = $KeycloakMemory }
                env = $envVars; probes = $probes
            })
            scale = @{ minReplicas = 1; maxReplicas = 1 }
        }
    }
}
$tempFile = Join-Path ([IO.Path]::GetTempPath()) "keycloak-$([guid]::NewGuid()).json"
try {
    $app | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $tempFile -Encoding utf8
    Write-Host 'Submitting Keycloak configuration with secret references, probes and origin restrictions...'
    Invoke-Az @('containerapp', 'create', '--name', $ContainerAppName, '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--yaml', $tempFile, '--output', 'none')
}
finally { Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue }
$RevisionName = Invoke-Az @('containerapp', 'show', '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--query', 'properties.latestRevisionName', '--output', 'tsv')
Wait-KeycloakRevision -RevisionName ($RevisionName -join '').Trim()
Write-Host 'Keycloak deployed with management probes and Front Door backend IP restrictions.'
Write-Warning 'All Front Door profiles share these ranges. This does not validate X-Azure-FDID. See README.'
