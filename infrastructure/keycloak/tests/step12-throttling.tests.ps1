# Offline throttling tests for the production CLI wrapper. No Azure calls/sleeps.
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../scripts/12-budget-alerts.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
$definition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-BudgetCli' }, $true)
. ([scriptblock]::Create($definition.Extent.Text))
function Start-Sleep { param($Seconds); $script:waited += $Seconds }
function az {
    $script:calls++
    if ($script:scenario -eq 'recover' -and $script:calls -ge 3) {
        $global:LASTEXITCODE = 0
        return '{"ok":true}'
    }
    $global:LASTEXITCODE = 1
    if ($script:scenario -eq 'permanent') { Write-Error 'ERROR: Forbidden'; return }
    Write-Error 'ERROR: Too Many Requests({"error":{"code":"429"}})'
}
foreach ($script:scenario in @('recover', 'exhausted', 'permanent')) {
    $script:calls = 0
    $script:waited = 0
    $caught = $null
    try { $result = Invoke-BudgetCli @('rest', '--method', 'get') } catch { $caught = $_ }
    switch ($scenario) {
        'recover' { if ($caught -or $calls -ne 3 -or $waited -ne 90 -or ($result | ConvertFrom-Json).ok -ne $true) { throw 'Recovery failed.' } }
        'exhausted' { if (-not $caught -or $calls -ne 5 -or $waited -ne 330) { throw 'Retry bound failed.' } }
        'permanent' { if (-not $caught -or $calls -ne 1 -or $waited -ne 0) { throw 'Permanent error must not retry.' } }
    }
}
Write-Host "PASS: HTTP 429 recovery, bounded retries and immediate failure for other errors (PowerShell $($PSVersionTable.PSVersion))."
