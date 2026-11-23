# Optionally apply a Standard WAF rate limit to both Front Door hostnames.
. "$PSScriptRoot/common.ps1"
if (-not $EnableWaf) {
    Write-Host 'Optional WAF skipped (EnableWaf = false). No policy is created.'
    return
}
if ($WafPolicyName -notmatch '^[a-zA-Z0-9]+$') { throw 'Set an alphanumeric WafPolicyName in local config (for example waffingauth).' }
if ($WafRateLimitPerMinute -lt 1) { throw 'WafRateLimitPerMinute must be positive.' }
# Read both domains before creating a billable policy. No front-door extension needed.
$domainId = Invoke-Az @('afd', 'custom-domain', 'show', '--resource-group', $ResourceGroup, '--profile-name', $FrontDoorProfile, '--custom-domain-name', $CustomDomainName, '--query', 'id', '-o', 'tsv')
$endpointId = Invoke-Az @('afd', 'endpoint', 'show', '--resource-group', $ResourceGroup, '--profile-name', $FrontDoorProfile, '--endpoint-name', $FrontDoorEndpoint, '--query', 'id', '-o', 'tsv')
$subscriptionId = Invoke-Az @('account', 'show', '--query', 'id', '-o', 'tsv')
$policyId = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Network/frontDoorWebApplicationFirewallPolicies/$WafPolicyName"
$policy = @{
    location = 'global'; sku = @{ name = 'Standard_AzureFrontDoor' }
    properties = @{
        policySettings = @{ enabledState = 'Enabled'; mode = 'Prevention' }
        customRules = @{ rules = @(@{
            name = 'LabRateLimit'; priority = 100; enabledState = 'Enabled'
            ruleType = 'RateLimitRule'; action = 'Block'
            rateLimitDurationInMinutes = 1; rateLimitThreshold = $WafRateLimitPerMinute
            matchConditions = @(@{ matchVariable = 'RequestUri'; operator = 'Contains'; matchValue = @('/'); negateCondition = $false })
        }) }
        managedRules = @{ managedRuleSets = @() }
    }
}
$tempFile = Join-Path ([IO.Path]::GetTempPath()) "waf-$([guid]::NewGuid()).json"
try {
    $policy | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $tempFile -Encoding utf8
    Invoke-Az @('rest', '--method', 'put', '--url', "${policyId}?api-version=2025-03-01", '--body', "@$tempFile", '--output', 'none')
    Invoke-Az @('resource', 'wait', '--ids', $policyId, '--api-version', '2025-03-01', '--created', '--timeout', '600')
    $association = @{
        'waf-policy' = @{ id = $policyId }
        associations = @(@{ domains = @(@{ id = $domainId }, @{ id = $endpointId }); 'patterns-to-match' = @('/*') })
    }
    $association | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tempFile -Encoding utf8
    Invoke-Az @('afd', 'security-policy', 'create', '--resource-group', $ResourceGroup,
        '--profile-name', $FrontDoorProfile, '--security-policy-name', $WafSecurityPolicyName,
        '--web-application-firewall', "@$tempFile", '--output', 'none')
}
finally { Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue }
Write-Host "Standard custom rate limit enabled on both domains: $WafRateLimitPerMinute requests per minute per source IP (approximate, distributed enforcement)."
