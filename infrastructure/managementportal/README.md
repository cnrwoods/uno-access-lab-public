# Management portal hosting

The portal runs on Azure App Service behind Azure Front Door, with policies in
Azure Blob Storage. The deployment workflow publishes application code; the
PowerShell wrapper provisions the hosting and storage resources.

## Provision the foundation

Copy `config.example.ps1` to the ignored `config.ps1`. Set the subscription and
review the resource names and public hostname. The resource group and Front
Door profile must already exist. Configuration holds identifiers only; keep
credentials in Key Vault.

Run from the repository root, reviewing the preview before applying:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File infrastructure/managementportal/Deploy-Hosting.ps1 -Mode ValidateOnly
powershell.exe -NoProfile -ExecutionPolicy Bypass -File infrastructure/managementportal/Deploy-Hosting.ps1 -Mode WhatIf
powershell.exe -NoProfile -ExecutionPolicy Bypass -File infrastructure/managementportal/Deploy-Hosting.ps1 -Mode Apply
```

The wrapper explicitly selects the configured subscription. `Apply` creates
billable resources through an incremental ARM deployment:

| Resource | Configuration |
| --- | --- |
| App Service plan | Linux Basic B1, one worker |
| Web app | .NET 10, managed identity, HTTPS, Always On, `/health/ready` probe |
| Application ingress | Allow `AzureFrontDoor.Backend` with the configured profile's exact `X-Azure-FDID`; deny other traffic |
| Deployment endpoint | Separate SCM rules; SCM and FTP basic publishing disabled |
| Storage | Standard LRS, HTTPS/TLS 1.2, shared-key and anonymous Blob access disabled |
| Recovery | Blob versioning and seven-day blob/container soft delete |
| Data role | App identity has Storage Blob Data Contributor on the `policies` container |

Storage is network-public and requires Microsoft Entra authorization. There is
no private endpoint or VNet integration. Blob versions persist until separately
removed; seven-day soft delete does not expire old versions.

An empty `StorageAccountName` uses the template's deterministic name. Set a
globally available name explicitly if needed. `-StorageOnly` provisions the
account and container while App Service quota is pending; rerun without it to
complete the foundation using the same configuration. App Service needs both
SKU quota and available Total Regional VMs.

Resources carry ownership tags. The wrapper refuses unowned name collisions,
implicit region/SKU changes and changes to an existing app's policy-storage
destination. For an existing owned app it preserves the app-settings dictionary,
including later Key Vault references. Use the wrapper for reruns: directly
deploying the template with `initializeAppSettings=true` replaces app settings.

The foundation does not deploy code, import policy data, set credentials,
configure public DNS or update Keycloak clients.

## Application deployment

[The GitHub workflow](../../.github/workflows/deploy-managementportal.yml) runs
both .NET console check suites, publishes the portal and deploys on pushes to
`main` or a manual run with `main` selected, when the repository variable
`DEPLOY_MANAGEMENTPORTAL` is `true`. Deployments are serialized; actions are
pinned to commits and checkout does not persist Git credentials.

Set repository secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
`AZURE_SUBSCRIPTION_ID` for a deployment identity using GitHub OIDC federation.
Grant Website Contributor at the web-app scope. The federated credential must
match the repository's actual OIDC subject and `main` ref, including any subject
customization. The workflow does not select a GitHub environment; adding one
changes the subject and requires a matching Azure credential.

The workflow's app and resource-group defaults describe this lab deployment.
Update its target and federation before using it for another Azure environment.
It deploys code only and preserves App Service settings. Application credentials
remain in Key Vault; they are not build inputs.

Set `DEPLOY_MANAGEMENTPORTAL=true` only after configuring the target, federation
and repository secrets. A new clone or public copy does not inherit GitHub
settings; deployment stays disabled until explicitly enabled.

## Hosted application configuration

Set the application settings described in the
[portal runbook](../../src/managementportal/README.md). In particular, configure
the Keycloak client secret and PIN shared key using Key Vault references. Grant
the app identity Key Vault Secrets User at the required secrets and retain its
container-scoped Blob data role. Keep the shared key stable when migrating
existing policies.

Configure a Front Door origin using the App Service hostname as both origin
hostname and Host header, HTTPS on port 443, and certificate-name validation.
Use `HEAD /health/live` for the origin probe. Route only the intended public
hostname, redirect HTTP to HTTPS, forward using HTTPS and disable caching.
Keep the application's public CNAME pointed directly at Front Door for managed
certificate renewal. DNS approval, route association and certificate deployment
are separate steps; verify the certificate before testing sign-in.

On the existing confidential Keycloak client, add exact hosted callbacks:

```text
https://<portal-hostname>/signin-oidc
https://<portal-hostname>/signout-callback-oidc
```

Use the hosted portal origin for Root URL and Home URL. Retain local callbacks
if local development is required. The realm JSON is a bootstrap template;
reimporting it is not an existing-realm migration procedure.

The lab's shared WAF restricts browser/device access to the bench network. The
portal also needs server-to-server Keycloak access. Its exception is limited to
the portal's current outbound IPs, the identity hostname, GET/POST and these
exact paths (with an optional query):

```text
/realms/fingauth/.well-known/openid-configuration
/realms/fingauth/protocol/openid-connect/certs
/realms/fingauth/protocol/openid-connect/token
/realms/fingauth/protocol/openid-connect/ext/par/request
```

The PAR endpoint is needed when pushed authorization is advertised. UserInfo is
disabled in the portal. Refresh outbound IP rules when App Service addresses
change. Export and preserve existing WAF rules and domain associations before
editing them. The Keycloak provisioning WAF script replaces policy contents and
must not be used to update this shared live policy.

When importing existing policy data, use a missing-destination precondition
(`If-None-Match: *`), verify the uploaded bytes and remove temporary operator
data-role assignments. Local and hosted stores remain separate after import.

## Verification

The hosted cutover was exercised with managed-identity Blob access, public
health/API probes, direct-origin blocking, browser sign-in with LinkedIn and a
passkey, sign-out, and the remote hardware sketch. Recheck these after changes:

- Run the .NET check suites in the top-level README and the release publish
  command in the portal runbook.
- Confirm `/health/live` and `/health/ready` succeed through Front Door.
- Confirm direct-origin requests are denied and TLS validates publicly.
- Check anonymous portal protection, real sign-in, role authorization and logout.
- Check policy reads/updates, active-policy selection and device operation.
- Confirm WAF permits intended clients and the portal's OIDC backchannel only.

The device API is anonymous within the lab network restriction. Device
authentication remains future work; this deployment is a proof of concept.
