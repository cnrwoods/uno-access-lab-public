# Offline regression checks: no Azure CLI calls or local operator config.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../config/config.example.ps1"
$AcrName = 'acrfingauthtest'
$KeyVaultName = 'kv-test'
$Subscription = 'test-subscription'
$path = Join-Path $PSScriptRoot '../scripts/02-identity-keyvault-acr.ps1'
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
if ($errors) { throw $errors }
# Replace only the config/helper import; execute the rest of the actual script.
$source = (Get-Content $path -Raw).Replace('. "$PSScriptRoot/common.ps1"', '')
$deployment = [scriptblock]::Create($source)
function Invoke-Az {
    param([string[]]$Arguments)
    $script:calls.Add(($Arguments -join ' '))
    if ($Arguments[0] -eq 'ad') {
        if ($Arguments -contains '--subscription') { throw 'Graph commands do not accept --subscription.' }
    }
    elseif ($Arguments[-2] -ne '--subscription' -or $Arguments[-1] -ne $Subscription) {
        throw 'Every resource call must select the configured subscription.'
    }
    $command = $Arguments[0..1] -join ' '
    switch ($command) {
        'identity list' { if ($script:scenario -eq 'fresh') { return }; return '/identity' }
        'identity create' { return }
        'identity show' { return '{"id":"/identity","principalId":"principal"}' }
        'keyvault list' {
            if ($script:scenario -eq 'lookup-failure') { throw 'Lookup failed' }
            if ($script:scenario -eq 'fresh') { return }
            return '/vault'
        }
        'keyvault create' { return }
        'keyvault show' { return '{"id":"/vault","properties":{"enableRbacAuthorization":true,"enablePurgeProtection":true}}' }
        'ad signed-in-user' { if ($script:scenario -eq 'user-failure') { throw 'Cannot resolve signed-in user' }; return 'user' }
        'acr list' { if ($script:scenario -in @('fresh', 'partial')) { return }; return '/registry' }
        'acr create' { return }
        'acr show' { return '{"id":"/registry","adminUserEnabled":false,"sku":{"name":"Basic"}}' }
        'role assignment' {
            if ($Arguments[2] -eq 'list') {
                if ($script:scenario -eq 'complete') { return '/assignment' }
                return
            }
            if ($Arguments[2] -eq 'create') {
                if ($script:scenario -eq 'role-failure') { throw 'Role assignment failed' }
                return
            }
            throw 'Unexpected role command'
        }
        default { throw "Unexpected Azure call: $command" }
    }
}
foreach ($script:scenario in @('fresh', 'partial', 'complete', 'lookup-failure', 'role-failure', 'user-failure')) {
    $script:calls = New-Object 'System.Collections.Generic.List[string]'
    $failed = $false
    try { & $deployment } catch { $failed = $true }
    if ($failed -ne ($scenario -in @('lookup-failure', 'role-failure', 'user-failure'))) { throw "Unexpected result for $scenario" }
    foreach ($kind in @('identity', 'keyvault')) {
        $count = @($calls | Where-Object { $_ -match "^$kind create " }).Count
        if ($count -ne [int]($scenario -eq 'fresh')) { throw "Incorrect first-run/reuse behavior for $kind." }
    }
    if ($scenario -eq 'user-failure' -and $calls.Count -ne 1) { throw 'User lookup must fail before resource operations.' }
    $registryCreates = @($calls | Where-Object { $_ -match '^acr create ' }).Count
    if ($registryCreates -ne [int]($scenario -in @('fresh', 'partial'))) { throw 'Incorrect registry resume behavior.' }
    $roleCreates = @($calls | Where-Object { $_ -match '^role assignment create ' }).Count
    if ($scenario -in @('fresh', 'partial') -and $roleCreates -ne 3) { throw 'Missing required role assignments.' }
    if ($scenario -eq 'complete' -and $roleCreates -ne 0) { throw 'Duplicated existing roles.' }
}
Write-Host "PASS: step 02 creates a fresh deployment, resumes partial deployment, reuses completed resources/roles, and reports lookup/permission failures (PowerShell $($PSVersionTable.PSVersion))."

