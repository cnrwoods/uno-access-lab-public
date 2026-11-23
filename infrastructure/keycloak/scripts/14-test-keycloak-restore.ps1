# Verify restored Keycloak data through the existing private Container Apps network.
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[a-z][a-z0-9-]{1,61}[a-z0-9]\z')]
    [string]$RestoredServerName,

    [ValidatePattern('\A[A-Za-z0-9._-]+\z')]
    [string]$LinkedInAlias = 'linkedin-openid-connect',

    [ValidateRange(0, 600)]
    [int]$WaitTimeoutSeconds = 300
)

. "$PSScriptRoot/common.ps1"

if ($RestoredServerName -eq $PostgresServerName) {
    throw 'RestoredServerName must identify a separate restored server; no Azure calls were made.'
}
foreach ($value in @($RealmName, $ManagementClientId)) {
    if ($value -cnotmatch '\A[A-Za-z0-9._-]+\z') {
        throw 'This restore check expects simple realm and client identifiers containing letters, numbers, dots, underscores or hyphens.'
    }
}

$serverQuery = '{id:id,name:name,state:state,fullyQualifiedDomainName:fullyQualifiedDomainName,network:network}'
$source = ConvertFrom-Json -InputObject ((Invoke-Az @('postgres', 'flexible-server', 'show',
    '--name', $PostgresServerName, '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--query', $serverQuery, '--output', 'json')) -join "`n")
$restored = ConvertFrom-Json -InputObject ((Invoke-Az @('postgres', 'flexible-server', 'show',
    '--name', $RestoredServerName, '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--query', $serverQuery, '--output', 'json')) -join "`n")
if (-not $source.id -or -not $restored.id -or $source.id -eq $restored.id -or $restored.state -ne 'Ready') {
    throw 'The restored server must be a separate Ready resource; no diagnostic job was created.'
}
if (-not $source.network.delegatedSubnetResourceId -or
    -not $source.network.privateDnsZoneArmResourceId -or
    $source.network.delegatedSubnetResourceId -ne $restored.network.delegatedSubnetResourceId -or
    $source.network.privateDnsZoneArmResourceId -ne $restored.network.privateDnsZoneArmResourceId) {
    throw 'The restored server must use the source server private subnet and DNS zone for this check; no diagnostic job was created.'
}
if ($restored.fullyQualifiedDomainName -cnotmatch '\A[a-z0-9-]+\.postgres\.database\.azure\.com\z') {
    throw 'Azure did not return the expected restored PostgreSQL hostname.'
}
$identityId = ((Invoke-Az @('identity', 'show', '--name', $ManagedIdentityName,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--query', 'id', '--output', 'tsv')) -join '').Trim()
$environmentId = ((Invoke-Az @('containerapp', 'env', 'show', '--name', $ContainerAppsEnvironment,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--query', 'id', '--output', 'tsv')) -join '').Trim()
if (-not $identityId -or -not $environmentId) { throw 'The existing managed identity or Container Apps environment was not found.' }

# A failed assertion makes psql exit nonzero, so execution status verifies the
# data even when this lab has no persistent console-log destination configured.
$sql = @'
BEGIN TRANSACTION READ ONLY;
DO $restore_check$
DECLARE
    realm_key text;
    provider_summary text;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.realm WHERE name = 'master') THEN
        RAISE EXCEPTION 'Restored master realm is missing';
    END IF;
    SELECT id INTO STRICT realm_key FROM public.realm WHERE name = '__REALM__';
    IF NOT EXISTS (SELECT 1 FROM public.client WHERE realm_id = realm_key AND client_id = '__CLIENT__') THEN
        RAISE EXCEPTION 'Restored portal client is missing';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.identity_provider WHERE realm_id = realm_key AND provider_alias = '__IDP__' AND enabled = true) THEN
        SELECT COALESCE(string_agg(provider_alias || ' (provider=' || provider_id || ', enabled=' || enabled::text || ')', ', ' ORDER BY provider_alias), '<none>')
            INTO provider_summary FROM public.identity_provider WHERE realm_id = realm_key;
        RAISE EXCEPTION 'Expected enabled identity provider alias "__IDP__" was not found. Restored providers: %', provider_summary;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.keycloak_role WHERE realm_id = realm_key AND name = 'platform-admin' AND client_role = false) THEN
        RAISE EXCEPTION 'Restored platform-admin realm role is missing';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public.user_entity WHERE realm_id = realm_key) THEN
        RAISE EXCEPTION 'Restored application realm has no users';
    END IF;
END;
$restore_check$;
COMMIT;
'@
$sql = $sql.Replace('__REALM__', $RealmName).Replace('__CLIENT__', $ManagementClientId).Replace('__IDP__', $LinkedInAlias)
# Keep a failing SQL client available long enough to collect its console error.
# Pass SQL as an argument, never interpolate SQL or credentials into shell code.
$runner = @'
psql --no-psqlrc --no-password --set ON_ERROR_STOP=1 --command "$1"
result=$?
printf '\nFINGAUTH_RESTORE_CHECK_EXIT=%s\n' "$result"
if [ "$result" -ne 0 ]; then
    sleep 90
fi
exit "$result"
'@
$runner = $runner.Replace("`r`n", "`n").Replace("`r", "`n")
$jobName = 'kc-restore-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$identities = @{}
$identities[$identityId] = @{}
$vaultBase = "https://$KeyVaultName.vault.azure.net/secrets"
$job = @{
    name = $jobName
    location = $Location
    identity = @{ type = 'UserAssigned'; userAssignedIdentities = $identities }
    properties = @{
        environmentId = $environmentId
        workloadProfileName = 'Consumption'
        configuration = @{
            triggerType = 'Manual'
            replicaTimeout = 180
            replicaRetryLimit = 0
            manualTriggerConfig = @{ parallelism = 1; replicaCompletionCount = 1 }
            secrets = @(
                @{ name = 'db-user'; keyVaultUrl = "$vaultBase/kc-db-username"; identity = $identityId }
                @{ name = 'db-pass'; keyVaultUrl = "$vaultBase/kc-db-password"; identity = $identityId }
            )
        }
        template = @{
            containers = @(@{
                name = 'restore-check'
                image = 'docker.io/library/postgres:17-alpine'
                command = @('/bin/sh')
                args = @('-c', $runner, 'restore-check', $sql)
                resources = @{ cpu = 0.25; memory = '0.5Gi' }
                env = @(
                    @{ name = 'PGHOST'; value = $restored.fullyQualifiedDomainName }
                    @{ name = 'PGDATABASE'; value = $PostgresDatabase }
                    @{ name = 'PGUSER'; secretRef = 'db-user' }
                    @{ name = 'PGPASSWORD'; secretRef = 'db-pass' }
                    @{ name = 'PGSSLMODE'; value = 'require' }
                    @{ name = 'PGCONNECT_TIMEOUT'; value = '15' }
                    @{ name = 'PGOPTIONS'; value = '-c default_transaction_read_only=on -c statement_timeout=15000' }
                )
            })
        }
    }
}

function Get-RestoreCheckConsole([string]$ExecutionName) {
    # In Windows PowerShell, redirecting native stderr turns CLI preview
    # warnings into ErrorRecords. Quiet optional reads must still finish;
    # Invoke-Az continues to throw explicitly for nonzero CLI exit codes.
    $ErrorActionPreference = 'Continue'
    try {
        $replicaJson = (Invoke-Az @('containerapp', 'job', 'replica', 'list', '--name', $jobName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--execution', $ExecutionName, '--query', '[].name', '--output', 'json') 2>$null) -join "`n"
        $replicas = @(ConvertFrom-Json -InputObject $replicaJson -ErrorAction Stop)
        if ($replicas.Count -eq 0 -or -not $replicas[0]) { return $null }
        $lines = @(Invoke-Az @('containerapp', 'job', 'logs', 'show', '--name', $jobName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--execution', $ExecutionName, '--replica', ([string]$replicas[0]), '--container', 'restore-check',
            '--tail', '100', '--format', 'text', '--only-show-errors') 2>$null)
        $console = $lines -join "`n"
        $marker = [regex]::Match($console, 'FINGAUTH_RESTORE_CHECK_EXIT=(\d+)')
        if ($marker.Success) {
            return @{ Text = $console; ExitCode = [int]$marker.Groups[1].Value }
        }
        return @{ Text = $console; ExitCode = $null }
    }
    catch {
        Write-Verbose 'Console logs are not available for this replica yet; execution status is still checked.'
        return $null
    }
}

function Show-RestoreCheckStartupDetails([string]$ExecutionName) {
    try {
        # Filter out environment variables and secret references from diagnostics.
        $details = Invoke-Az @('containerapp', 'job', 'replica', 'list', '--name', $jobName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--execution', $ExecutionName,
            '--query', '[].{name:name,containers:properties.containers[].{name:name,runningState:runningState,details:runningStateDetails,exitCode:exitCode,reason:reason,message:message}}', '--output', 'json')
        Write-Host 'Replica startup details:'
        Write-Host ($details -join "`n")
    }
    catch { Write-Warning 'Replica startup details could not be retrieved.' }
}

Write-Host "Restored server: $RestoredServerName"
Write-Host "Check: master + $RealmName realms, $ManagementClientId client, $LinkedInAlias provider, platform-admin role and application users."
if (-not $PSCmdlet.ShouldProcess($RestoredServerName, "Create and run temporary private database check job $jobName")) { return }

$tempFile = Join-Path ([IO.Path]::GetTempPath()) "keycloak-restore-check-$([guid]::NewGuid()).json"
$createAttempted = $false
try {
    # Windows PowerShell 5.1 Set-Content UTF8 emits a BOM. The Azure CLI YAML
    # loader uses the Windows default encoding, which can turn that BOM into
    # stray characters and make otherwise valid JSON fail YAML parsing.
    [IO.File]::WriteAllText($tempFile, ($job | ConvertTo-Json -Depth 25), (New-Object Text.UTF8Encoding($false)))
    # Cleanup also handles a resource created by Azure before a CLI create error.
    $createAttempted = $true
    $null = Invoke-Az @('containerapp', 'job', 'create', '--name', $jobName,
        '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--yaml', $tempFile, '--output', 'none')
    $execution = ((Invoke-Az @('containerapp', 'job', 'start', '--name', $jobName,
        '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--query', 'name', '--output', 'tsv')) -join '').Trim()
    if (-not $execution) { throw 'Azure did not return a job execution name.' }
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitTimeoutSeconds)
    do {
        $status = ((Invoke-Az @('containerapp', 'job', 'execution', 'show', '--name', $jobName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--job-execution-name', $execution, '--query', 'properties.status', '--output', 'tsv')) -join '').Trim()
        if ($status -eq 'Succeeded') {
            Write-Host 'PASS: the restored database is reachable and contains the expected Keycloak data. Browser sign-in and full data comparison are separate checks.'
            return
        }
        $console = Get-RestoreCheckConsole -ExecutionName $execution
        if ($null -ne $console -and $null -ne $console.ExitCode -and $console.ExitCode -ne 0) {
            Write-Host 'Database check console output:'
            Write-Host $console.Text
            throw "The read-only database check exited with code $($console.ExitCode). Its PostgreSQL error is shown above. No Keycloak upgrade was performed."
        }
        if ($status -in @('Failed', 'Stopped', 'Degraded')) {
            if ($null -ne $console -and $console.Text) {
                Write-Host 'Database check console output:'
                Write-Host $console.Text
            }
            Show-RestoreCheckStartupDetails -ExecutionName $execution
            throw "Restore check $execution ended with status $status. Console and startup details were requested above. No Keycloak upgrade was performed."
        }
        $remaining = ($deadline - [DateTime]::UtcNow).TotalSeconds
        if ($remaining -le 0) { break }
        Write-Host "Waiting for restore check: $status"
        Start-Sleep -Seconds ([Math]::Min(10, [Math]::Ceiling($remaining)))
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Restore check did not succeed within $WaitTimeoutSeconds seconds. No Keycloak upgrade was performed."
}
finally {
    Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    if ($createAttempted) {
        try {
            $null = Invoke-Az @('containerapp', 'job', 'delete', '--name', $jobName,
                '--resource-group', $ResourceGroup, '--subscription', $Subscription, '--yes', '--output', 'none')
            Write-Host "Removed temporary job $jobName."
        }
        catch { Write-Warning "Temporary job cleanup failed. Delete only the diagnostic job '$jobName' in '$ResourceGroup' after inspecting it." }
    }
}
