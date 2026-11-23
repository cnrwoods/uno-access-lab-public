# Offline tests for the real readiness function; no Azure calls or waiting.
$ErrorActionPreference = 'Stop'
$file = Join-Path $PSScriptRoot '../scripts/06-deploy-keycloak.ps1'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors)
if ($errors) { throw $errors }
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-KeycloakRevision' }, $true)
. ([scriptblock]::Create($definition.Extent.Text))
$ContainerAppName = 'test-app'
$ResourceGroup = 'test-group'
$Subscription = 'test-subscription'
function Start-Sleep { param($Seconds) }
function Invoke-Az {
    param([string[]]$Arguments)
    if ($Arguments -notcontains '--subscription') { throw 'Subscription was omitted.' }
    $script:reads++
    switch ($script:scenario) {
        'max-scale' { return '{"properties":{"healthState":"Healthy","runningState":"RunningAtMaxScale"}}' }
        'failed' { return '{"properties":{"provisioningState":"Failed","runningState":"Failed","runningStateDetails":"Image pull failed"}}' }
        'timeout' { return '{"properties":{"healthState":"None","runningState":"Activating"}}' }
        'starting' { if ($script:reads -eq 1) { return '{"properties":{"healthState":"None","runningState":"Activating"}}' } }
    }
    return '{"properties":{"healthState":"Healthy","runningState":"Running"}}'
}
foreach ($script:scenario in @('healthy', 'max-scale', 'starting', 'failed', 'timeout')) {
    $script:reads = 0
    $caught = $null
    try { Wait-KeycloakRevision -RevisionName 'test-revision' -TimeoutSeconds $(if ($scenario -eq 'timeout') { 0 } else { 30 }) }
    catch { $caught = $_ }
    if ([bool]$caught -ne ($scenario -in @('failed', 'timeout'))) { throw "Unexpected result: $scenario" }
    if ($scenario -eq 'starting' -and $reads -ne 2) { throw 'Did not wait for startup.' }
    if ($scenario -eq 'failed' -and $caught.ToString() -notmatch 'Image pull failed') { throw 'Lost failure details.' }
}
Write-Host "PASS: readiness success, delayed startup, failure and timeout (PowerShell $($PSVersionTable.PSVersion))."
