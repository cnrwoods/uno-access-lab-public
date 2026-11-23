# Execute the real image scripts with a fake CLI. No Azure calls, builds or sleeps.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../config/config.example.ps1"
$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AcrName = 'acrfingauthtest'
$Subscription = 'test-subscription'
$ResourceGroup = 'test-group'
$ContainerAppName = 'test-keycloak'
$script:Digest = 'sha256:' + ('a' * 64)
$script:Image = "acrfingauthtest.azurecr.io/keycloak@$script:Digest"
$script:OldImage = "acrfingauthtest.azurecr.io/keycloak:$KeycloakVersion-fingauth-v0"

function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function ArgumentValue([string[]]$Arguments, [string]$Name) {
    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -lt 0) { throw "Required argument $Name was omitted." }
    return $Arguments[$index + 1]
}
function Start-Sleep { param($Seconds); $script:Waited += $Seconds }
function Invoke-Az {
    param([string[]]$Arguments)
    $script:Calls.Add([string[]]$Arguments)
    Assert ((ArgumentValue $Arguments '--subscription') -eq $Subscription) 'The configured subscription was not explicitly selected.'
    switch ($Arguments[0..1] -join ' ') {
        'acr build' { return }
        'acr show' { return 'acrfingauthtest.azurecr.io' }
        'acr repository' {
            Assert ($Arguments[2] -eq 'show') 'Only a registry image lookup is allowed.'
            Assert ((ArgumentValue $Arguments '--image') -eq 'keycloak:26.8.2-fingauth-v1') 'The requested tag was not looked up.'
            switch ($script:Scenario) {
                'missing-digest' { return }
                'bad-digest' { return 'not-a-digest' }
                'lookup-failure' { throw 'Registry lookup failed.' }
            }
            return $script:Digest
        }
        'containerapp show' {
            $container = @{ name = 'keycloak'; image = $script:OldImage; env = @(@{ name = 'KC_DB_PASSWORD'; secretRef = 'existing-db-secret' }) }
            $containers = @($container)
            switch ($script:Scenario) {
                'missing-container' { $containers = @() }
                'multiple-containers' { $containers = @($container, @{ name = 'sidecar'; image = 'sidecar:1' }) }
                'wrong-container' { $container.name = 'other' }
                'missing-image' { $container.image = '' }
                'already-current' { $container.image = $script:Image }
            }
            $mode = 'Single'
            if ($script:Scenario -eq 'multiple-mode') { $mode = 'Multiple' }
            return (@{ revision = 'old-revision'; mode = $mode; containers = $containers } | ConvertTo-Json -Depth 8)
        }
        'containerapp update' {
            # Narrow CLI arguments are the preservation contract: never recreate
            # the app or submit environment/secret/ingress/probe configuration.
            $allowedFlags = @('--name', '--resource-group', '--subscription', '--container-name', '--image', '--query', '--output')
            foreach ($argument in $Arguments) {
                if ($argument.StartsWith('--')) { Assert ($argument -in $allowedFlags) "Unexpected mutation argument $argument." }
            }
            Assert ($Arguments.Count -eq 16) 'Unexpected arguments were added to the image-only mutation.'
            Assert ((ArgumentValue $Arguments '--container-name') -eq 'keycloak') 'The existing container was not selected.'
            Assert ((ArgumentValue $Arguments '--image') -ceq $script:Image) 'The rollout image was not pinned to the registry digest.'
            if ($script:Scenario -eq 'update-failure') { throw 'Update failed.' }
            if ($script:Scenario -eq 'missing-revision') { return }
            if ($script:Scenario -eq 'old-revision') { return 'old-revision' }
            return 'new-revision'
        }
        'containerapp revision' {
            Assert ($Arguments[2] -eq 'show') 'Only revision inspection is allowed.'
            Assert ((ArgumentValue $Arguments '--revision') -eq 'new-revision') 'The previous revision was used as proof of readiness.'
            $script:RevisionReads++
            $status = @{ healthState = 'Healthy'; runningState = 'Running'; template = @{ containers = @(@{ name = 'keycloak'; image = $script:Image }) } }
            switch ($script:Scenario) {
                'delayed' {
                    if ($script:RevisionReads -eq 1) { $status.healthState = 'None'; $status.runningState = 'Activating' }
                }
                'max-scale' { $status.runningState = 'RunningAtMaxScale' }
                'revision-failure' { $status.provisioningState = 'Failed'; $status.runningState = 'Failed'; $status.runningStateDetails = 'Image pull failed' }
                'contradictory-failure' { $status.provisioningState = 'Failed'; $status.runningStateDetails = 'Provisioning failed despite stale healthy flags' }
                'timeout' { $status.healthState = 'None'; $status.runningState = 'Activating' }
                'revision-image-mismatch' { $status.template.containers[0].image = $script:OldImage }
            }
            return (@{ properties = $status } | ConvertTo-Json -Depth 8)
        }
        default { throw "Unexpected CLI call: $($Arguments[0..1] -join ' ')" }
    }
}

$path = Join-Path $ProjectRoot 'scripts/13-update-keycloak-image.ps1'
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$parseErrors)
Assert (-not $parseErrors) 'Image rollout script has PowerShell parse errors.'
$deployment = [scriptblock]::Create((Get-Content -LiteralPath $path -Raw).Replace('. "$PSScriptRoot/common.ps1"', ''))

$beforeUpdateFailures = @('missing-digest', 'bad-digest', 'lookup-failure', 'missing-container', 'multiple-containers', 'wrong-container', 'missing-image', 'multiple-mode')
$afterUpdateFailures = @('update-failure', 'missing-revision', 'old-revision', 'revision-failure', 'contradictory-failure', 'timeout', 'revision-image-mismatch')
foreach ($script:Scenario in (@('healthy', 'delayed', 'max-scale', 'what-if', 'already-current') + $beforeUpdateFailures + $afterUpdateFailures)) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    $script:RevisionReads = 0
    $script:Waited = 0
    $caught = $null
    try {
        if ($script:Scenario -eq 'what-if') { & $deployment -ImageTag '26.8.2-fingauth-v1' -RunningKeycloakVersion $KeycloakVersion -WhatIf }
        else { & $deployment -ImageTag '26.8.2-fingauth-v1' -RunningKeycloakVersion $KeycloakVersion -ReadinessTimeoutSeconds $(if ($script:Scenario -eq 'timeout') { 0 } else { 660 }) }
    }
    catch { $caught = $_ }
    $mustFail = $script:Scenario -in ($beforeUpdateFailures + $afterUpdateFailures)
    Assert ([bool]$caught -eq $mustFail) "Unexpected outcome for $script:Scenario : $caught"
    $updates = @($script:Calls | Where-Object { $_[0] -eq 'containerapp' -and $_[1] -eq 'update' })
    $shouldUpdate = $script:Scenario -notin ($beforeUpdateFailures + @('what-if', 'already-current'))
    Assert ($updates.Count -eq [int]$shouldUpdate) "Unexpected mutation count for $script:Scenario."
    if ($script:Scenario -in @('what-if', 'already-current', 'missing-revision', 'old-revision', 'update-failure')) {
        Assert ($script:RevisionReads -eq 0) "Unexpected readiness polling for $script:Scenario."
    }
    if ($script:Scenario -eq 'delayed') { Assert ($script:RevisionReads -eq 2 -and $script:Waited -eq 10) 'The rollout did not wait for startup.' }
    if ($script:Scenario -eq 'timeout') { Assert ($script:RevisionReads -eq 1 -and $script:Waited -eq 0) 'Readiness timeout was not bounded.' }
}

foreach ($badTag in @('latest', $KeycloakVersion, 'invalid/tag', "invalid`n", ('a' * 129))) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    $caught = $null
    try { & $deployment -ImageTag $badTag -RunningKeycloakVersion $KeycloakVersion } catch { $caught = $_ }
    Assert ($caught -and $script:Calls.Count -eq 0) 'An unsafe rollout tag was not rejected before Azure lookups.'
}

foreach ($runningVersion in @('26.7.4', '26.8.1', 'latest', "26.8.2`n")) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    $caught = $null
    try { & $deployment -ImageTag '26.8.2-fingauth-v1' -RunningKeycloakVersion $runningVersion } catch { $caught = $_ }
    Assert ($caught -and $script:Calls.Count -eq 0) 'A different or invalid running engine version was not rejected before Azure lookups.'
}

$buildPath = Join-Path $ProjectRoot 'scripts/04-build-keycloak-image.ps1'
$build = [scriptblock]::Create((Get-Content -LiteralPath $buildPath -Raw).Replace('. "$PSScriptRoot/common.ps1"', ''))
foreach ($customTag in @($null, '26.8.2-fingauth-v1')) {
    $script:Calls = New-Object 'System.Collections.Generic.List[object]'
    if ($null -eq $customTag) { & $build } else { & $build -ImageTag $customTag }
    Assert ($script:Calls.Count -eq 1 -and $script:Calls[0][0] -eq 'acr' -and $script:Calls[0][1] -eq 'build') 'The build script made an unexpected CLI call.'
    $expectedTag = if ($null -eq $customTag) { $KeycloakVersion } else { $customTag }
    Assert ((ArgumentValue $script:Calls[0] '--image') -eq "keycloak:$expectedTag") 'The build did not preserve the initial tag or use the explicit custom tag.'
}
$script:Calls = New-Object 'System.Collections.Generic.List[object]'
$caught = $null
try { & $build -ImageTag 'registry.example/keycloak:tag' } catch { $caught = $_ }
Assert ($caught -and $script:Calls.Count -eq 0) 'The build accepted a full image reference instead of a Docker tag.'

$savedVersion = $KeycloakVersion
$script:Calls = New-Object 'System.Collections.Generic.List[object]'
$caught = $null
try {
    $KeycloakVersion = '0.0.0'
    & $build -ImageTag '26.8.2-fingauth-v1'
}
catch { $caught = $_ }
finally { $KeycloakVersion = $savedVersion }
Assert ($caught -and $script:Calls.Count -eq 0 -and $caught.ToString().Contains('FROM stages must pin')) 'A stale local KeycloakVersion was not rejected before submitting the ACR build.'

# ACR receives this directory, including Git-ignored files. Keep its allowlist
# restricted to the recipe and theme tree so local configuration cannot enter it.
$patterns = @(Get-Content (Join-Path $ProjectRoot '.dockerignore') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
Assert ($patterns[0] -eq '**') 'The build context must deny files by default.'
$allow = @('!Containerfile', '!.dockerignore', '!themes/', '!themes/**')
Assert ($patterns.Count -eq 5 -and @($patterns | Where-Object { $_ -ne '**' -and $_ -notin $allow }).Count -eq 0) 'The build context allows files outside the recipe and themes.'
foreach ($pattern in $allow) { Assert ($patterns -contains $pattern) "Required build context entry $pattern is missing." }

Write-Host "PASS: same-engine guard, image-only digest rollout, subscription selection, WhatIf, fail-closed inputs, readiness, tagged builds and restricted ACR context (PowerShell $($PSVersionTable.PSVersion))."
