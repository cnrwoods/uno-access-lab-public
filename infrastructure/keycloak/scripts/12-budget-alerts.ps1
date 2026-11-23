# Create or update one monthly budget covering both lab resource groups.
param(
    [ValidateRange(0.01, 1000000)][decimal]$MonthlyAmount = 50,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$BudgetName = 'fingauth_budget',
    [switch]$Preview
)
. "$PSScriptRoot/common.ps1"

# A budget sends notifications; it does not stop resources or cap spending.
$recipients = @($BudgetAlertEmails | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { throw 'Set BudgetAlertEmails in config/config.ps1 before running step 12.' }
    $value = $_.Trim()
    try { $address = [System.Net.Mail.MailAddress]::new($value) }
    catch { throw 'Supply valid email addresses in BudgetAlertEmails in config/config.ps1.' }
    if ($address.Address -ne $value) { throw 'Use bare email addresses without display names.' }
    $value
} | Sort-Object -Unique)
if ($recipients.Count -lt 1 -or $recipients.Count -gt 5) { throw 'Set between one and five BudgetAlertEmails in config/config.ps1.' }

function Invoke-BudgetCli {
    param([string[]]$Arguments)
    # Capture stderr separately: warnings/errors must never be parsed as JSON.
    # az rest doesn't expose response headers unless debug logging is enabled;
    # use bounded backoff without enabling potentially sensitive debug output.
    $errorFile = Join-Path ([IO.Path]::GetTempPath()) "budget-error-$([guid]::NewGuid()).txt"
    try {
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $PSNativeCommandUseErrorActionPreference = $false
                $raw = & az @Arguments 2> $errorFile
                $code = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $previousPreference }
            $errorText = Get-Content -LiteralPath $errorFile -Raw -ErrorAction SilentlyContinue
            if ($code -eq 0) { return $raw }
            if ($errorText -notmatch '(?i)\b429\b|TooManyRequests|Too Many Requests') {
                throw "Azure budget request failed (exit $code): $errorText"
            }
            if ($attempt -eq 5) {
                throw 'Azure is still throttling the budget request after five attempts. Wait several minutes before rerunning step 12. Budget completion has not been verified.'
            }
            $delay = [int][Math]::Min(120, 30 * [Math]::Pow(2, $attempt - 1))
            Write-Warning "Azure returned HTTP 429. Retrying budget request in $delay seconds (attempt $($attempt + 1)/5)."
            # Keep each individual sleep bounded while allowing a longer backoff.
            for ($remaining = $delay; $remaining -gt 0; $remaining -= 30) {
                Start-Sleep -Seconds ([Math]::Min(30, $remaining))
            }
        }
    }
    finally { Remove-Item -LiteralPath $errorFile -ErrorAction SilentlyContinue }
}

function Invoke-BudgetRest {
    param([string]$Method, [string]$Url, $Body)
    $arguments = @('rest', '--method', $Method, '--url', $Url, '--output', 'json')
    $tempFile = $null
    try {
        if ($null -ne $Body) {
            $tempFile = Join-Path ([IO.Path]::GetTempPath()) "fingauth-budget-$([guid]::NewGuid()).json"
            $Body | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $tempFile -Encoding utf8
            $arguments += @('--body', "@$tempFile")
        }
        $raw = Invoke-BudgetCli $arguments
        if ($raw) { return (ConvertFrom-Json -InputObject ($raw -join "`n")) }
    }
    finally { if ($tempFile) { Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue } }
}

$subscriptionId = Invoke-Az @('account', 'show', '--subscription', $Subscription, '--query', 'id', '-o', 'tsv')
$subscriptionId = ($subscriptionId -join '').Trim()
if (-not $subscriptionId) { throw 'Could not resolve the configured subscription.' }
$scope = "https://management.azure.com/subscriptions/$subscriptionId"

# Read the actual managed group; never reconstruct its generated name.
$managedGroup = Invoke-Az @('containerapp', 'env', 'show', '--name', $ContainerAppsEnvironment,
    '--resource-group', $ResourceGroup, '--subscription', $Subscription,
    '--query', 'properties.infrastructureResourceGroup', '-o', 'tsv')
$managedGroup = ($managedGroup -join '').Trim()
if (-not $managedGroup) { throw 'Container Apps managed resource group not found. Run step 05 first; refusing an incomplete budget filter.' }
$groups = @($ResourceGroup, $managedGroup | Sort-Object -Unique)
foreach ($group in $groups) {
    $null = Invoke-Az @('group', 'show', '--name', $group, '--subscription', $Subscription, '--query', 'id', '-o', 'tsv')
}

# Azure inherits the subscription currency; GBP is assumed for this lab.
# Use the chosen amount directly without currency lookups or conversion.
$currency = 'GBP'

$budgetCollectionUrl = "$scope/providers/Microsoft.Consumption/budgets?api-version=2024-08-01"
$existing = $null
$nextUrl = $budgetCollectionUrl
while ($nextUrl) {
    $page = Invoke-BudgetRest 'get' $nextUrl $null
    foreach ($item in $page.value) {
        if ($item.name -eq $BudgetName) { $existing = $item }
    }
    $nextUrl = $page.nextLink
}
$now = [DateTime]::UtcNow
$start = [DateTime]::new($now.Year, $now.Month, 1, 0, 0, 0, [DateTimeKind]::Utc)
$period = @{ startDate = $start.ToString('yyyy-MM-ddTHH:mm:ssZ'); endDate = $start.AddYears(10).ToString('yyyy-MM-ddTHH:mm:ssZ') }
if ($existing) {
    if ($existing.properties.category -ne 'Cost' -or $existing.properties.timeGrain -ne 'Monthly') {
        throw 'The chosen budget name is already used for a different budget type. Choose another -BudgetName.'
    }
    # Preserve the original period when updating so monthly reruns do not drift it.
    $period = $existing.properties.timePeriod
    if ([DateTime]$period.endDate -le $now) { throw 'This budget has expired. Choose a new BudgetName or extend its dates in Cost Management.' }
}
$notifications = @{}
foreach ($threshold in @(50, 80, 100)) {
    $notifications["Actual$threshold"] = @{
        enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = $threshold
        thresholdType = 'Actual'; contactEmails = $recipients
        contactRoles = @(); contactGroups = @(); locale = 'en-gb'
    }
}
$notifications['Forecast100'] = @{
    enabled = $true; operator = 'GreaterThanOrEqualTo'; threshold = 100
    thresholdType = 'Forecasted'; contactEmails = $recipients
    contactRoles = @(); contactGroups = @(); locale = 'en-gb'
}
$budget = @{
    properties = @{
        category = 'Cost'; amount = $MonthlyAmount; timeGrain = 'Monthly'; timePeriod = $period
        filter = @{ dimensions = @{ name = 'ResourceGroupName'; operator = 'In'; values = $groups } }
        notifications = $notifications
    }
}
if ($existing) { $budget['eTag'] = $existing.eTag }
Write-Host "Combined monthly budget: $MonthlyAmount $currency across $($groups -join ', ')."
Write-Host "Actual alerts: 50%, 80%, 100%; forecast alert: 100%. Expires: $($period.endDate)."
if ($Preview) {
    $budget | ConvertTo-Json -Depth 25
    Write-Host 'Preview only: no budget created or updated.'
    return
}
$budgetUrl = "$scope/providers/Microsoft.Consumption/budgets/${BudgetName}?api-version=2024-08-01"
$null = Invoke-BudgetRest 'put' $budgetUrl $budget
$saved = Invoke-BudgetRest 'get' $budgetUrl $null
$savedGroups = @($saved.properties.filter.dimensions.values | Sort-Object -Unique)
if ($saved.properties.amount -ne $MonthlyAmount -or $saved.properties.timeGrain -ne 'Monthly' -or
    $saved.properties.filter.dimensions.name -ne 'ResourceGroupName' -or
    ($savedGroups -join '|') -ne ($groups -join '|') -or
    @($saved.properties.notifications.PSObject.Properties).Count -ne 4) {
    throw 'Azure returned an unexpected budget configuration. Inspect the budget in Cost Management.'
}
foreach ($key in $notifications.Keys) {
    $actual = $saved.properties.notifications.$key
    $expected = $notifications[$key]
    if (-not $actual.enabled -or $actual.threshold -ne $expected.threshold -or
        $actual.thresholdType -ne $expected.thresholdType -or
        $actual.operator -ne $expected.operator -or
        (@($actual.contactEmails | Sort-Object -Unique) -join '|') -ne ($recipients -join '|')) {
        throw "Saved notification '$key' does not match the requested alert. Inspect the budget in Cost Management."
    }
}
Write-Host "Budget '$BudgetName' saved and verified. Alerts will be emailed to the supplied recipients."
Write-Warning 'Budget alerts are delayed notifications, not a hard spending cap. Resources keep running after the threshold is exceeded.'
