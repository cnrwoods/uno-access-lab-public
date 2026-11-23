# Replace ingress IP rules with the current Front Door backend IPv4 ranges.
. "$PSScriptRoot/common.ps1"

# Replace the entire allow list in one PATCH, removing retired ranges without
# ever clearing restrictions first. This script owns ALL ACA ingress IP rules.
$restrictions = @(Get-FrontDoorRestrictions)
$appId = Invoke-Az @('containerapp', 'show', '--name', $ContainerAppName, '--resource-group', $ResourceGroup, '--query', 'id', '-o', 'tsv')
if (-not $appId) { throw 'Container App not found.' }
$body = @{ location = $Location; properties = @{ configuration = @{ ingress = @{ ipSecurityRestrictions = $restrictions } } } }
$tempFile = Join-Path ([IO.Path]::GetTempPath()) "origin-restrictions-$([guid]::NewGuid()).json"
try {
    $body | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tempFile -Encoding utf8
    Invoke-Az @('rest', '--method', 'patch', '--url', "${appId}?api-version=2025-01-01", '--body', "@$tempFile", '--output', 'none')
}
finally { Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue }
Invoke-Az @('resource', 'wait', '--ids', $appId, '--api-version', '2025-01-01', '--updated', '--timeout', '600')
$fdid = Invoke-Az @('afd', 'profile', 'show', '--resource-group', $ResourceGroup, '--profile-name', $FrontDoorProfile, '--query', 'frontDoorId', '-o', 'tsv')
Write-Host "Refreshed $($restrictions.Count) IPv4 backend prefixes. Front Door profile ID: $fdid"
Write-Warning 'X-Azure-FDID is NOT enforced. Another Front Door profile can reach this origin.'
Write-Host 'Repeat weekly and before resuming a dormant lab; verify public discovery and direct-origin denial.'
