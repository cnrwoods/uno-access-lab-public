# Copy this file to config.ps1 and edit the values marked CHANGE_ME.
# config.ps1 is gitignored.

$Subscription = "CHANGE_ME"

$ResourceGroup = "RG-fingauth.conorwoods.co.uk"
$Location = "uksouth"

$VnetName = "vnet-fingauth-uks"
$VnetCidr = "10.30.0.0/16"

$ContainerAppsSubnet = "snet-aca"
$ContainerAppsSubnetCidr = "10.30.0.0/23"

$PrivateEndpointSubnet = "snet-private-endpoints"
$PrivateEndpointSubnetCidr = "10.30.2.0/24"

$PostgresSubnet = "snet-postgresql"
$PostgresSubnetCidr = "10.30.3.0/24"

$ContainerAppsEnvironment = "cae-fingauth"
$ContainerAppName = "ca-keycloak"
$ManagedIdentityName = "id-keycloak"

# Must be globally unique. Lowercase/alphanumeric constraints apply.
$KeyVaultName = "CHANGE_ME"
# ACR: 5-50 lowercase letters/digits only; NO dashes. Must be globally unique.
$AcrName = "CHANGE_ME"
$PostgresServerName = "CHANGE_ME"

$PostgresAdminUser = "kcdbadmin"
$PostgresDatabase = "keycloak"

$KeycloakVersion = "26.8.2"
$KeycloakImageName = "keycloak"

$FrontDoorProfile = "afd-fingauth"
$FrontDoorEndpoint = "fingauth-idp"
$FrontDoorOriginGroup = "og-keycloak"
$FrontDoorOrigin = "origin-keycloak"
$FrontDoorRoute = "route-keycloak"

$WafPolicyName = "waffingauth"
$WafSecurityPolicyName = "sp-fingauth"

$PublicHostname = "idp.fingauth.conorwoods.co.uk"
$ManagementPlaneUrl = "https://manage.fingauth.conorwoods.co.uk"

$RealmName = "fingauth"
$ManagementClientId = "fingerprint-management-plane"

# Low-cost lab sizing. Increase to 1 CPU / 2Gi if memory or login latency suffers.
$KeycloakCpu = 0.5
$KeycloakMemory = "1Gi"
# Optional Standard custom-rule WAF; adds charges when enabled.
$EnableWaf = $false
$WafRateLimitPerMinute = 1000
$CustomDomainName = "idp-fingauth-conorwoods"

# Budget alert recipients for step 12. Replace the empty value with your email.
$BudgetAlertEmails = @('')
