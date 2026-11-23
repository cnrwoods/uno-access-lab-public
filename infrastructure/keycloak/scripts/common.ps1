# Load local configuration and shared deployment helpers.
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptRoot
$ConfigPath = Join-Path $ProjectRoot "config/config.ps1"

if (-not (Test-Path $ConfigPath)) {
    throw "Missing $ConfigPath. Copy config/config.example.ps1 to config/config.ps1 and edit it first."
}

# Defaults also apply to an existing local config; do not overwrite operator values.
$KeycloakCpu = 0.5
$KeycloakMemory = "1Gi"
$EnableWaf = $false
$WafRateLimitPerMinute = 1000
$CustomDomainName = "idp-fingauth-conorwoods"
. $ConfigPath

function Invoke-Az {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        # Arguments can contain passwords. Never echo them in an exception.
        throw "Azure CLI command failed (exit $LASTEXITCODE). Review the preceding error without sharing secrets."
    }
}

function Get-FrontDoorRestrictions {
    # ACA accepts IPv4 CIDRs, not service-tag names. Fetch fresh global ranges;
    # location selects the service-tag API version, not a regional subset.
    $data = Invoke-Az @("network", "list-service-tags", "--location", $Location, "--subscription", $Subscription, "-o", "json")
    $tag = @(($data | ConvertFrom-Json).values | Where-Object name -eq "AzureFrontDoor.Backend")
    if ($tag.Count -ne 1) { throw "Front Door backend service tag missing; refusing an unrestricted ingress configuration." }
    $prefixes = @($tag[0].properties.addressPrefixes | Where-Object { $_ -notmatch ':' } | Sort-Object -Unique)
    if ($prefixes.Count -eq 0) { throw "No IPv4 Front Door backend prefixes returned." }
    $index = 0
    foreach ($prefix in $prefixes) {
        if ($prefix -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$' -or $prefix -eq '0.0.0.0/0') {
            throw "Unexpected service-tag prefix; refusing ingress update."
        }
        @{ name = "afd-backend-$index"; ipAddressRange = $prefix; action = "Allow";
           description = "AzureFrontDoor.Backend snapshot; refresh with step 08" }
        $index++
    }
}

function Get-PlaintextFromSecureString {
    param(
        [Parameter(Mandatory = $true)]
        [Security.SecureString]$SecureString
    )

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}
