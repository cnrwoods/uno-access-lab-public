# Build the pinned, optimised Keycloak image in ACR.
[CmdletBinding()]
param(
    [ValidatePattern('\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z')]
    [string]$ImageTag
)

. "$PSScriptRoot/common.ps1"

$Containerfile = Join-Path $ProjectRoot "Containerfile"
function Assert-KeycloakBaseVersion([string]$Path, [string]$ExpectedVersion) {
    $stages = @(Get-Content -LiteralPath $Path | Where-Object { $_ -match '^\s*FROM\s+' })
    if ($stages.Count -ne 2) { throw 'Expected exactly two pinned Keycloak FROM stages in Containerfile; no ACR build was submitted.' }
    foreach ($stage in $stages) {
        $match = [regex]::Match($stage.Trim(), '\AFROM[ \t]+quay\.io/keycloak/keycloak:(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?:[ \t]+AS[ \t]+[A-Za-z0-9_-]+)?\z', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success -or $match.Groups['version'].Value -cne $ExpectedVersion) {
            throw "Both Containerfile FROM stages must pin quay.io/keycloak/keycloak:$ExpectedVersion. Update the local config/config.ps1 KeycloakVersion to match the intended image version before building; no ACR build was submitted."
        }
    }
}
Assert-KeycloakBaseVersion -Path $Containerfile -ExpectedVersion $KeycloakVersion
if (-not $PSBoundParameters.ContainsKey('ImageTag')) { $ImageTag = $KeycloakVersion }
if ($ImageTag -notmatch '\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z') { throw 'ImageTag must be a Docker tag, without a registry or repository path.' }
$ImageReference = "$KeycloakImageName`:$ImageTag"

Invoke-Az @(
    "acr", "build",
    "--registry", $AcrName,
    "--subscription", $Subscription,
    "--image", $ImageReference,
    "--file", $Containerfile,
    $ProjectRoot
)

Write-Host "Built $AcrName.azurecr.io/$ImageReference"
