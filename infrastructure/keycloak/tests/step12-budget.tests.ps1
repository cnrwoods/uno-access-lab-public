# Offline script tests. No Azure connection, operator config or email delivery.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../config/config.example.ps1"
$Subscription = 'test-subscription'
$ResourceGroup = 'lab-main'
$BudgetAlertEmails = @('operator@example.com')
$path = Join-Path $PSScriptRoot '../scripts/12-budget-alerts.ps1'
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
if ($errors) { throw $errors }
$source = (Get-Content $path -Raw).Replace('. "$PSScriptRoot/common.ps1"', '')
$deployment = [scriptblock]::Create($source)
function az { $global:LASTEXITCODE = 0; Invoke-Az -Arguments $args }
function Invoke-Az {
    param([string[]]$Arguments)
    switch ($Arguments[0]) {
        'account' { return 'test-subscription-id' }
        'containerapp' { if ($script:scenario -eq 'missing-group') { return }; return 'ME_actual-managed-group' }
        'group' { return '/test/group' }
        'rest' {
            $method = $Arguments[[Array]::IndexOf($Arguments, '--method') + 1]
            $url = $Arguments[[Array]::IndexOf($Arguments, '--url') + 1]
            if ($method -eq 'post') { throw 'Currency/cost queries must not be made.' }
            if ($method -eq 'get' -and $url -match '/budgets\?') {
                if ($script:scenario -eq 'update') {
                    return '{"value":[{"name":"fingauth_budget","eTag":"original-etag","properties":{"category":"Cost","timeGrain":"Monthly","timePeriod":{"startDate":"2026-01-01T00:00:00Z","endDate":"2036-01-01T00:00:00Z"}}}]}'
                }
                return '{"value":[]}'
            }
            if ($method -eq 'put') {
                $script:puts++
                $file = $Arguments[[Array]::IndexOf($Arguments, '--body') + 1].TrimStart('@')
                $script:saved = Get-Content -LiteralPath $file -Raw
                return $script:saved
            }
            if ($method -eq 'get') { return $script:saved }
            throw 'Unexpected REST request.'
        }
        default { throw 'Unexpected Azure command.' }
    }
}
foreach ($script:scenario in @('fresh', 'update', 'preview', 'missing-group')) {
    $script:puts = 0
    $script:saved = $null
    $errorText = $null
    try { & $deployment -Preview:($scenario -eq 'preview') | Out-Null }
    catch { $errorText = $_.ToString() }
    $mustFail = $scenario -eq 'missing-group'
    if ([bool]$errorText -ne $mustFail) { throw "Unexpected result for ${scenario}: $errorText" }
    if ($puts -ne [int]($scenario -in @('fresh', 'update'))) { throw "Wrong budget writes for $scenario" }
    if ($puts) {
        $budget = $saved | ConvertFrom-Json
        if ($budget.properties.amount -ne 50 -or $budget.properties.timeGrain -ne 'Monthly') { throw 'Monthly budget must be 50.' }
        $filter = $budget.properties.filter.dimensions
        if ($filter.name -ne 'ResourceGroupName' -or $filter.operator -ne 'In' -or
            ($filter.values | Sort-Object) -join '|' -ne 'lab-main|ME_actual-managed-group') { throw 'Must combine the two actual groups in one OR filter.' }
        foreach ($threshold in @(50,80,100)) {
            $rule = $budget.properties.notifications."Actual$threshold"
            if ($rule.threshold -ne $threshold -or $rule.thresholdType -ne 'Actual' -or
                $rule.contactEmails[0] -ne 'operator@example.com' -or -not $rule.enabled) { throw 'Incorrect actual alert.' }
        }
        if ($budget.properties.notifications.Forecast100.thresholdType -ne 'Forecasted') { throw 'Missing forecast warning.' }
        if ($scenario -eq 'update' -and ($budget.eTag -ne 'original-etag' -or $budget.properties.timePeriod.startDate -ne '2026-01-01T00:00:00Z')) { throw 'Update lost concurrency token or original start date.' }
    }
}
Write-Host "PASS: combined 50/month budget, thresholds, forecast, update, preview, no currency lookup and missing-group guard (PowerShell $($PSVersionTable.PSVersion))."
