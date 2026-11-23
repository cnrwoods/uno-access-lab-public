# Roll out a theme image using the same Keycloak engine version.
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z')]
    [string]$ImageTag,

    # Verify this in Admin Console -> Server info before invoking the helper.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$RunningKeycloakVersion,

    [ValidateRange(0, 660)]
    [int]$ReadinessTimeoutSeconds = 660
)

. "$PSScriptRoot/common.ps1"

# Single revision mode can overlap old and new replicas during startup. Engine
# upgrades need a separate maintenance procedure that stops every old instance.
if ($RunningKeycloakVersion -cne $KeycloakVersion) {
    throw "This helper supports same-engine theme updates only. Verified running Keycloak version '$RunningKeycloakVersion' differs from the configured target '$KeycloakVersion'. Use the maintenance upgrade procedure; no Azure calls were made."
}

function Wait-KeycloakImageRevision {
    param(
        [Parameter(Mandatory = $true)][string]$RevisionName,
        [Parameter(Mandatory = $true)][string]$ExpectedImage,
        [ValidateRange(0, 660)][int]$TimeoutSeconds = 660
    )

    if ([string]::IsNullOrWhiteSpace($RevisionName)) { throw 'Azure did not return a new revision name.' }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $json = Invoke-Az @('containerapp', 'revision', 'show', '--name', $ContainerAppName,
            '--resource-group', $ResourceGroup, '--subscription', $Subscription,
            '--revision', $RevisionName, '--output', 'json')
        $revision = ConvertFrom-Json -InputObject ($json -join "`n")
        $status = $revision.properties
        $containers = @($status.template.containers)
        if ($containers.Count -ne 1 -or $containers[0].name -cne 'keycloak' -or $containers[0].image -cne $ExpectedImage) {
            throw "Revision '$RevisionName' does not contain the expected Keycloak image. Inspect it before proceeding."
        }
        if ($status.provisioningState -in @('Failed', 'Deprovisioned') -or
            $status.runningState -in @('Failed', 'Degraded', 'Stopped')) {
            throw "Keycloak revision '$RevisionName' failed: $($status.runningStateDetails). Inspect Container Apps logs and review database compatibility before recovering with a previous server image."
        }
        if ($status.healthState -eq 'Healthy' -and $status.runningState -in @('Running', 'RunningAtMaxScale')) {
            Write-Host "Revision '$RevisionName' is Healthy and Running with the requested image."
            return
        }
        $remaining = ($deadline - [DateTime]::UtcNow).TotalSeconds
        if ($remaining -le 0) { break }
        Write-Host "Waiting for '$RevisionName': health=$($status.healthState), running=$($status.runningState)."
        Start-Sleep -Seconds ([Math]::Min(10, [Math]::Ceiling($remaining)))
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Keycloak revision '$RevisionName' did not become healthy within $TimeoutSeconds seconds. Inspect Container Apps logs; no automatic rollback was performed."
}

function Format-RolloutArgument([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

if ($ImageTag -eq 'latest' -or $ImageTag -eq $KeycloakVersion) {
    throw 'Use a distinct versioned build tag, for example 26.8.2-fingauth-v1; latest and the initial provisioning tag are not accepted.'
}

$registry = (Invoke-Az @('acr', 'show', '--name', $AcrName, '--subscription', $Subscription,
    '--query', 'loginServer', '--output', 'tsv') -join '').Trim()
if ($registry -notmatch '^[a-z0-9][a-z0-9.-]*[a-z0-9]$') { throw 'Azure did not return a valid registry login server.' }

$digest = (Invoke-Az @('acr', 'repository', 'show', '--name', $AcrName,
    '--image', "$KeycloakImageName`:$ImageTag", '--subscription', $Subscription,
    '--query', 'digest', '--output', 'tsv') -join '').Trim().ToLowerInvariant()
if ($digest -notmatch '^sha256:[a-f0-9]{64}$') { throw 'The requested registry image did not resolve to a SHA256 digest; no update was submitted.' }
$image = "$registry/$KeycloakImageName@$digest"

$json = Invoke-Az @('containerapp', 'show', '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--query', '{revision:properties.latestRevisionName,mode:properties.configuration.activeRevisionsMode,containers:properties.template.containers}',
    '--output', 'json')
$current = ConvertFrom-Json -InputObject ($json -join "`n")
$containers = @($current.containers)
if ($containers.Count -ne 1 -or $containers[0].name -cne 'keycloak') {
    throw 'Expected exactly one existing container named keycloak; no update was submitted.'
}
if ($current.mode -ne 'Single') { throw 'This rollout expects the existing Single revision mode; review the app before updating it.' }
$previousImage = [string]$containers[0].image
$previousRevision = [string]$current.revision
if ([string]::IsNullOrWhiteSpace($previousImage) -or [string]::IsNullOrWhiteSpace($previousRevision)) {
    throw 'The existing image or revision name is missing; no update was submitted.'
}
if ($previousImage -ceq $image) {
    Write-Host "The keycloak container already uses $image. No update was submitted."
    return
}

Write-Host "Current revision: $previousRevision"
Write-Host "Current image: $previousImage"
Write-Host "Requested tag: $ImageTag"
Write-Host "Resolved image: $image"
# This command is a previous-image reference, not a database rollback. Reuse
# current configuration because old revisions may reference retired secrets.
$rollbackArguments = @('containerapp', 'update', '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--container-name', 'keycloak', '--image', $previousImage, '--output', 'none')
Write-Warning 'Image updates can migrate the database schema. This script does not create backups or coordinate server-version upgrades. Restoring an older server after a schema migration requires a matching database restore; an image-only rollback is insufficient.'
Write-Host ('Previous-image command (use only with a database compatible with that server version): az ' + (($rollbackArguments | ForEach-Object { Format-RolloutArgument $_ }) -join ' '))

if (-not $PSCmdlet.ShouldProcess("$ContainerAppName in $ResourceGroup ($Subscription)", "Update only the keycloak container image to $image")) { return }

$revisionName = (Invoke-Az @('containerapp', 'update', '--name', $ContainerAppName,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--container-name', 'keycloak', '--image', $image,
    '--query', 'properties.latestRevisionName', '--output', 'tsv') -join '').Trim()
if ([string]::IsNullOrWhiteSpace($revisionName) -or $revisionName -ceq $previousRevision) {
    throw 'Azure did not return a different revision after the image update. Inspect the app; the previous revision was not used as proof of rollout readiness.'
}
Wait-KeycloakImageRevision -RevisionName $revisionName -ExpectedImage $image -TimeoutSeconds $ReadinessTimeoutSeconds
