# Prepare Azure CLI extensions and resource providers.
. "$PSScriptRoot/common.ps1"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI was not found in PATH."
}

Write-Host "Signing in to Azure if required..."
az account show *> $null
if ($LASTEXITCODE -ne 0) {
    az login
    if ($LASTEXITCODE -ne 0) { throw "az login failed." }
}

Invoke-Az @("account", "set", "--subscription", $Subscription)

Write-Host "Current Azure context:"
Invoke-Az @('account', 'show', '--query', '{subscription:name, subscriptionId:id, tenantId:tenantId, user:user.name}', '-o', 'table')

Write-Host "Installing/updating Container Apps and Front Door CLI extensions..."
Invoke-Az @('extension', 'add', '--name', 'containerapp', '--upgrade', '--yes')
Invoke-Az @('extension', 'add', '--name', 'cdn', '--upgrade', '--yes')

$providers = @(
    "Microsoft.App",
    "Microsoft.Cdn",
    "Microsoft.ContainerRegistry",
    "Microsoft.KeyVault",
    "Microsoft.DBforPostgreSQL",
    "Microsoft.Network",
    "Microsoft.ManagedIdentity",
    "Microsoft.OperationalInsights"
)

foreach ($provider in $providers) {
    Write-Host "Registering $provider..."
    Invoke-Az @('provider', 'register', '--namespace', $provider, '--wait')
}

Write-Host "Prerequisites complete."
