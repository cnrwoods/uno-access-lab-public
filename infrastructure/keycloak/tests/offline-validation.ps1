# Run in PowerShell 7. No real Azure CLI calls, credentials or local config used.
$ErrorActionPreference = 'Stop'
$source = Split-Path $PSScriptRoot -Parent
foreach ($file in Get-ChildItem $source -Filter *.ps1 -Recurse | Where-Object FullName -NotMatch '[\\/]config[\\/]config.ps1$') {
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$parseErrors)
    if ($parseErrors) { throw "Parse errors in $($file.Name): $parseErrors" }
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$fixture = Join-Path ([IO.Path]::GetTempPath()) "keycloak-offline-$([guid]::NewGuid())"
$global:KeycloakOffline = @{ Calls = [Collections.Generic.List[object]]::new(); Payloads = [Collections.Generic.List[object]]::new(); BadFeed = $false }
function global:az {
    $global:LASTEXITCODE = 0
    $a = @($args)
    $state = $global:KeycloakOffline
    $state.Calls.Add($a)
    $cmd = $a -join ' '
    foreach ($flag in @('--yaml', '--body', '--formatted-custom-domains', '--web-application-firewall')) {
        $index = [Array]::IndexOf($a, $flag)
        if ($index -ge 0) {
            $path = $a[$index + 1].TrimStart('@')
            $state.Payloads.Add((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json))
        }
    }
    switch -Regex ($cmd) {
        '^network list-service-tags ' {
            if ($state.BadFeed) { return '{"values":[]}' }
            return '{"values":[{"name":"AzureFrontDoor.Backend","properties":{"addressPrefixes":["192.0.2.0/24","2001:db8::/32","192.0.2.0/24","198.51.100.0/24"]}}]}'
        }
        '^network vnet subnet show ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.Network/virtualNetworks/vnet/subnets/aca' }
        '^identity show ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id' }
        '^containerapp env show ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.App/managedEnvironments/env' }
        '^containerapp revision show ' { return '{"properties":{"healthState":"Healthy","runningState":"Running"}}' }
        '^containerapp show .*--query id ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.App/containerApps/app' }
        '^containerapp show ' { return 'origin.example.azurecontainerapps.io' }
        '^afd custom-domain show .*--query id ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.Cdn/profiles/afd/customDomains/idp' }
        '^afd custom-domain show ' { return '{"id":"/subscriptions/test/resourceGroups/lab/providers/Microsoft.Cdn/profiles/afd/customDomains/idp","domainValidationState":"Approved","validationProperties":{"validationToken":"fake-token"}}' }
        '^afd endpoint show .*--query id ' { return '/subscriptions/test/resourceGroups/lab/providers/Microsoft.Cdn/profiles/afd/afdEndpoints/endpoint' }
        '^afd endpoint show ' { return 'example.azurefd.net' }
        '^afd profile show ' { return 'fake-fdid' }
        '^account show ' { return 'test' }
        '^(containerapp (create|env create)|afd (profile create|endpoint create|origin-group create|origin create|route (create|update)|custom-domain create|security-policy create)|rest |resource wait )' { return }
        default { throw "Unexpected fake CLI call: $cmd" }
    }
}
try {
    $null = New-Item -ItemType Directory -Path "$fixture/scripts", "$fixture/config" -Force
    Copy-Item "$source/scripts/*.ps1" "$fixture/scripts"
    Copy-Item "$source/config/config.example.ps1" "$fixture/config/config.ps1"
    & "$fixture/scripts/05-container-app-environment.ps1"
    & "$fixture/scripts/06-deploy-keycloak.ps1"
    $app = $global:KeycloakOffline.Payloads[0]
    Assert ($app.properties.configuration.ingress.ipSecurityRestrictions.Count -eq 2) 'CIDRs must be IPv4 and deduplicated.'
    Assert (-not $app.properties.configuration.ingress.allowInsecure) 'Ingress must require HTTPS.'
    Assert ($app.properties.configuration.ingress.targetPort -eq 8080) 'Only the application port can be exposed.'
    $container = $app.properties.template.containers[0]
    Assert ($container.resources.cpu -eq 0.5 -and $container.resources.memory -eq '1Gi') 'Lab resource defaults differ.'
    Assert ($container.probes.Count -eq 3 -and @($container.probes | Where-Object { $_.httpGet.port -ne 9000 }).Count -eq 0) 'Management probes must all use 9000.'
    Assert ($app.properties.configuration.secrets.Count -eq 4) 'Initial configuration must contain all secret references.'
    Assert (@($app.properties.configuration.secrets | Where-Object { $_.value -or -not $_.identity -or $_.keyVaultUrl -notmatch '/secrets/[^/]+$' }).Count -eq 0) 'Only versionless vault references with identity are allowed.'
    Assert (($container.env | Where-Object name -eq KC_HOSTNAME).value -eq 'https://idp.fingauth.conorwoods.co.uk') 'Canonical hostname mismatch.'
    Assert ($app.properties.template.scale.minReplicas -eq 1 -and $app.properties.template.scale.maxReplicas -eq 1) 'Keep one warm replica.'
    & "$fixture/scripts/07-front-door.ps1"
    & "$fixture/scripts/08-origin-hardening.ps1"
    $patch = $global:KeycloakOffline.Payloads[1]
    Assert ($patch.properties.configuration.ingress.ipSecurityRestrictions.Count -eq 2 -and $patch.location) 'Refresh must replace rules atomically with location included.'
    $before = $global:KeycloakOffline.Payloads.Count
    $global:KeycloakOffline.BadFeed = $true
    foreach ($step in @('06-deploy-keycloak.ps1', '08-origin-hardening.ps1')) {
        $failed = $false
        try { & "$fixture/scripts/$step" } catch { $failed = $true }
        Assert $failed 'Missing service-tag data must fail closed.'
    }
    Assert ($global:KeycloakOffline.Payloads.Count -eq $before) 'Bad feed must never submit a mutation payload.'
    $global:KeycloakOffline.BadFeed = $false
    & "$fixture/scripts/09-custom-domain.ps1"
    & "$fixture/scripts/09-custom-domain.ps1" -Associate
    $before = $global:KeycloakOffline.Calls.Count
    & "$fixture/scripts/10-waf.ps1"
    Assert ($global:KeycloakOffline.Calls.Count -eq $before) 'Disabled WAF must make no Azure calls.'
    Add-Content "$fixture/config/config.ps1" '$EnableWaf = $true'
    & "$fixture/scripts/10-waf.ps1"
    $waf = $global:KeycloakOffline.Payloads[$global:KeycloakOffline.Payloads.Count - 2]
    $association = $global:KeycloakOffline.Payloads[$global:KeycloakOffline.Payloads.Count - 1]
    Assert ($waf.sku.name -eq 'Standard_AzureFrontDoor' -and $waf.properties.managedRules.managedRuleSets.Count -eq 0) 'WAF must use Standard custom rules only.'
    Assert ($association.associations[0].domains.Count -eq 2) 'WAF must cover both domains.'
    $calls = $global:KeycloakOffline.Calls | ForEach-Object { $_ -join ' ' }
    Assert (@($calls | Where-Object { $_ -match '^afd route create ' -and $_ -match 'HttpsOnly' }).Count -eq 1) 'Origin traffic must always be HTTPS.'
    $realm = Get-Content "$source/realm/realm-fingauth.json" -Raw | ConvertFrom-Json
    $client = $realm.clients[0]
    Assert ($client.standardFlowEnabled -and -not $client.implicitFlowEnabled -and -not $client.directAccessGrantsEnabled -and $client.attributes.'pkce.code.challenge.method' -eq 'S256') 'Browser client security settings changed.'
    Assert (-not $client.publicClient -and $client.clientAuthenticatorType -eq 'client-secret' -and -not $client.serviceAccountsEnabled) 'The server-side portal must be a confidential code-flow client without a service account.'
    Assert (-not $client.PSObject.Properties['secret']) 'A client secret must never be committed in the realm template.'
    Assert (-not $client.fullScopeAllowed) 'The portal must filter role scopes.'
    Assert ($client.redirectUris.Count -eq 1 -and $client.redirectUris[0] -eq 'https://localhost:7080/signin-oidc' -and $client.attributes.'post.logout.redirect.uris' -eq 'https://localhost:7080/signout-callback-oidc') 'Local callbacks must use exact HTTPS paths.'
    $roleScope = @($realm.scopeMappings | Where-Object client -eq $client.clientId)
    Assert ($roleScope.Count -eq 1 -and (($roleScope[0].roles | Sort-Object) -join ',') -eq 'platform-admin,platform-auditor,platform-operator') 'The portal role scope must contain only application roles.'
    $roleMapper = @($client.protocolMappers | Where-Object protocolMapper -eq 'oidc-usermodel-realm-role-mapper')
    Assert ($roleMapper.Count -eq 1 -and $roleMapper[0].config.'claim.name' -eq 'roles' -and $roleMapper[0].config.multivalued -eq 'true' -and $roleMapper[0].config.'id.token.claim' -eq 'true' -and $roleMapper[0].config.'access.token.claim' -eq 'false') 'The portal must receive assigned realm roles as a multivalued ID-token claim.'
    Write-Host 'PASS: parsing, deployment payload, probes, secret references, fail-closed ingress, HTTPS route, domain association, WAF, confidential client, PKCE and scoped ID-token roles.'
}
finally {
    Remove-Item Function:\az -ErrorAction SilentlyContinue
    Remove-Variable KeycloakOffline -Scope Global -ErrorAction SilentlyContinue
    # Only delete the generated fixture, after checking its resolved parent/name.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ((Split-Path $resolved -Parent) -eq $tempRoot -and (Split-Path $resolved -Leaf) -like 'keycloak-offline-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
