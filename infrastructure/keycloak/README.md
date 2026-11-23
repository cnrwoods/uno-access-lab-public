# Fing Auth identity infrastructure

Keycloak provides identity for the management portal. The Azure lab uses
Container Apps in UK South, private PostgreSQL and Front Door Standard. Public
DNS is managed separately in Route 53.

```text
Browser -- HTTPS --> Front Door -- HTTPS --> Container Apps ingress
                                             | HTTP :8080
                                          Keycloak
                                             | TLS
                                     Private PostgreSQL
```

The container image targets Keycloak **26.8.2**. Both `Containerfile` stages and
`$KeycloakVersion` must agree. This target does not imply that an existing server
has been upgraded; see the [theme and upgrade runbook](themes/README.md).

| Component | Lab configuration |
| --- | --- |
| Keycloak | One warm replica, 0.5 CPU / 1 GiB, Consumption profile |
| PostgreSQL 17 | B1ms, 32 GiB with auto-grow, seven-day backups; no HA or geo-redundant backup |
| Registry | Basic, registry admin credentials disabled |
| Key Vault | RBAC, purge protection, 90-day soft delete; authenticated public endpoint |
| Managed identity | AcrPull on the registry; Key Vault Secrets User on the vault |
| Front Door | Standard, HTTPS to origin, no authentication-route caching |
| Logs | Live Container Apps logs; no retained Log Analytics history |
| Network | `10.30.0.0/16`; Container Apps `/23`, PostgreSQL `10.30.3.0/24` |

`10.30.2.0/24` is reserved for private endpoints; none are provisioned.

## Provisioning

Use PowerShell 7 and Azure CLI from `infrastructure/keycloak`. Copy
`config/config.example.ps1` to ignored `config/config.ps1`. Set the subscription,
globally unique resource names, hostnames and budget recipients. Passwords are
prompted securely in step 03 and saved in Key Vault.

```powershell
. .\config\config.ps1
az account set --subscription $Subscription
az account show --query '{subscription:name,id:id}' -o table
```

Some initial scripts use the active CLI subscription. Verify it before every
deployment session. The deployment account needs resource creation and role
assignment permissions; budgets also require subscription budget read/write.
Run scripts individually to inspect results and resume partial provisioning.
They create billable Azure resources.

| Step | Script | Purpose |
| --- | --- | --- |
| 00 | `00-prereqs.ps1` | Sign in, prepare extensions and register providers |
| 01 | `01-foundation.ps1` | Resource group, VNet and delegated subnets |
| 02 | `02-identity-keyvault-acr.ps1` | Identity, vault, registry and role assignments |
| 03 | `03-postgresql.ps1` | Private database and database/bootstrap secrets |
| 04 | `04-build-keycloak-image.ps1` | Build the pinned image in ACR |
| 05 | `05-container-app-environment.ps1` | External Container Apps environment |
| 06 | `06-deploy-keycloak.ps1` | Initial configuration, probes and restricted ingress |
| 07 | `07-front-door.ps1` | Front Door origin and route |
| 08 | `08-origin-hardening.ps1` | Refresh backend IPv4 restrictions |
| 09 | `09-custom-domain.ps1` | Domain validation and route association |
| 10 | `10-waf.ps1` | Optional custom rate-limit WAF |
| 11 | `11-remove-bootstrap-secrets.ps1` | Retire bootstrap references after admin setup |
| 12 | `12-budget-alerts.ps1` | Combined resource-group budget |
| 13 | `13-update-keycloak-image.ps1` | Same-engine theme rollout |
| 14 | `14-test-keycloak-restore.ps1` | Read-only checks against a separate restored database |

These are provisioning scripts, not general migration tools. Step 02 supports
reuse. Step 03 is not a password-rotation/resizing script; its
`-ResumeExistingServer` option requires the original password, a Ready server and
a matching administrator. Step 06 restores initial bootstrap references and
must not be rerun after bootstrap cleanup. Step 10 replaces policy contents;
do not use it to edit the shared portal/identity WAF.

Step 06 submits secrets, probes and ingress restrictions together and waits up
to 11 minutes for a healthy revision. Health probes use private port 9000;
only application port 8080 is exposed. The public HTTPS hostname is fixed with
`KC_HOSTNAME` and proxy headers use `xforwarded`.

Step 09 prints TXT validation and CNAME records; it does not update public DNS.
After Azure approves the domain, run `scripts/09-custom-domain.ps1 -Associate`.
Keep the CNAME directly on Front Door for certificate renewal. Verify the public
certificate and route before using the admin console.

## Realm and administrator setup

Keep `master` for Keycloak administration. Import
[realm-fingauth.json](realm/realm-fingauth.json) as a new application realm.
The template contains no credentials or users and defines:

- Realm `fingauth` and `platform-admin`, `platform-operator`, `platform-auditor` roles.
- Confidential client `fingerprint-management-plane`, authorization code flow,
  PKCE S256 and exact local HTTPS callbacks on `https://localhost:7080`.
- Restricted role scopes and a multivalued `roles` ID-token claim.

Implicit flow, direct access grants and service accounts are disabled. Configure
the existing client manually when updating an existing realm; importing the JSON
again is not a migration procedure. Add hosted callbacks as described in the
[portal hosting runbook](../managementportal/README.md#hosted-application-configuration).

Set the client's credential in the portal's user secrets or hosted Key Vault
reference as `Authentication:ClientSecret`. It is separate from any LinkedIn
provider secret. Configure LinkedIn in the realm independently; its broker
callback is on the identity hostname. Assign `platform-admin` explicitly to
portal administrators. Operator and auditor roles alone do not grant portal
access. Verify the generated ID token contains the intended application role,
then test an actual portal login.

Before removing the temporary administrator, create and test a permanent
administrator in `master`, with its admin role and authenticator MFA. Keep the
existing session open while testing a new private-browser session. Configure
OTP through Required user actions, complete enrolment, then verify a fresh
login asks for both password and OTP. Enrolment on one user does not enforce it
for every administrator.

Delete the temporary user in Keycloak, then run step 11 to remove bootstrap
environment variables, Container Apps secret references and backing Key Vault
secrets. The script asks for `REMOVE`; it does not delete the user. Deleted vault
secrets remain recoverable during retention. Keep old revisions inactive.

## Origin security and lab limits

Container Apps accepts IPv4 allowlists but does not enforce `X-Azure-FDID`.
Steps 06/08 fetch `AzureFrontDoor.Backend` ranges and fail closed for a missing
feed. Refresh the snapshot weekly and before resuming a dormant lab:

```powershell
.\scripts\08-origin-hardening.ps1
```

Step 08 replaces every ingress IP rule, including manual exceptions. Ordinary
direct-origin traffic is blocked, but another Front Door profile can reach the
origin because backend ranges are shared. Hostname settings and a subnet NSG
do not close this gap. A proxy enforcing FDID or Premium/Private Link would be
needed for stronger isolation.

The initial optional WAF offers a custom per-IP rate limit (1,000 requests/minute
by default), without managed rules or bot protection. Disabling it in config
does not remove an existing policy. The shared live WAF has additional network
restrictions and scoped portal backchannel access; preserve those when editing it.

This lab uses the PostgreSQL administrator for application connections, one
Keycloak replica and live logs only. A dedicated database account, retained
audit logs, restricted administrator access, recovery exercises and load testing
are prerequisites for a production design.

## Budget and operations

Step 12 includes the primary and Azure-managed infrastructure resource groups.
Set one to five `BudgetAlertEmails` in ignored config, then preview and apply:

```powershell
.\scripts\12-budget-alerts.ps1 -Preview
.\scripts\12-budget-alerts.ps1
```

The default monthly amount is 50, assuming GBP; Azure uses the subscription's
currency. Actual alerts fire at 50%, 80% and 100%, with a forecast alert at 100%.
The budget appears at subscription scope, preserves existing period dates and
expires after ten years. It excludes Route 53 and other resource groups. Alerts
do not stop resources or cap spending. PostgreSQL, Front Door, the warm replica,
registry and storage continue accruing charges.

PostgreSQL can be stopped between lab sessions, making Keycloak unavailable.
Storage still costs money and Azure automatically restarts the server after
seven days. Use `az postgres flexible-server stop/start` with the configured
resource group and server name.

Run all offline checks from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File infrastructure/keycloak/tests/offline-validation.ps1
Get-ChildItem infrastructure/keycloak/tests/*.tests.ps1 | ForEach-Object { & $_.FullName }
```

These use mocks and temporary fixtures, without deploying or sending email.
`tests/oidc-smoke.ps1` is a separate live discovery check using local config.
After deployment, verify public discovery, valid TLS, direct-origin denial,
permanent administrator/MFA access, local credentials, LinkedIn login and portal
role authorization. Mock checks do not establish live availability or a working
browser session.

For a failed revision, inspect console/system logs, vault RBAC propagation,
registry access and database connectivity. For edge 502/503, also check the
backend-range snapshot and origin Host header. Domain approval can precede
certificate/edge deployment.
