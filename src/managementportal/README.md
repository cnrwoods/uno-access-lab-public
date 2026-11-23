# Management portal

The .NET 10 Razor Pages app hosts the administration UI and [read-only policy API](../policyapi/README.md). Browser access uses Keycloak OpenID Connect and requires the `platform-admin` realm role. Policies use local JSON files or Azure Blob Storage.

## Local sign-in setup

Install the .NET 10 SDK and configure a confidential Keycloak client with **Client authentication**, **Standard flow** and PKCE S256. The repository defaults use realm `fingauth` and client `fingerprint-management-plane` at `https://idp.fingauth.conorwoods.co.uk/realms/fingauth`. Set `Authentication__Authority` and `Authentication__ClientId` for your own realm; the public project endpoint does not grant portal access.

Configure the local client URLs:

| Setting | Value |
| --- | --- |
| Root URL / Home URL | `https://localhost:7080/` |
| Valid redirect URI | `https://localhost:7080/signin-oidc` |
| Valid post logout redirect URI | `https://localhost:7080/signout-callback-oidc` |

Assign `platform-admin` to an authorized user. Include a multivalued string `roles` claim in the ID token using a **User Realm Role** mapper with blank prefix. Restrict the client's role scope rather than enabling full scope. The [Keycloak guide](../../infrastructure/keycloak/README.md#realm-and-administrator-setup) covers the project realm and LinkedIn identity broker; another OIDC provider must emit the same role claim.

From the repository root, save the Keycloak client's secret in .NET user secrets and trust the development certificate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Initialize-PortalAuthentication.ps1
dotnet dev-certs https --trust
```

The helper prompts securely and sends the secret to the .NET CLI through standard input. It does not print it or place it in command arguments. Use the Keycloak client secret, not a LinkedIn identity-provider secret. The project's `UserSecretsId` is `ManagementPortal-fingauth-local`.

## PIN setup

Provision a random 32-byte registration key shared by the app and one bench UNO:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Initialize-PocDevice.ps1
```

By default this creates two Git-ignored files:

- `src/managementportal/App_Data/device-shared-key.hex` for the server.
- `hardware/arduino/SEN0359/fingerprint_Matching_inos/fingerprint_and_pin_withRemotePolicy/arduino_secrets.h` for the original remote-policy sketch.

The script retains existing matching files and Wi-Fi settings and never prints the key. Configure Wi-Fi and the computer's LAN address in the ignored header. When using [E2E_local](../../hardware/arduino/E2E_local/README.md) or [E2E_remote](../../hardware/arduino/E2E_remote/README.md), follow that sketch's setup guide and use the same existing registration key. Restart the app after provisioning.

`PolicySecurity__SharedKeyHex` overrides the file with a 64-character hex value; `PolicySecurity__SharedKeyFile` selects another key file. With no key, fingerprint-only and deny-all policies remain available, but PIN creation is disabled. Replacing a key requires reprovisioning the device and setting each policy's PIN again. Keep real keys, Wi-Fi settings and policy files out of Git, logs and screenshots.

PINs contain 4–8 ASCII digits, retaining leading zeroes. The server stores only a salted HMAC-SHA256 verifier, which the UNO checks locally. The key is never included in policy JSON. See the [PIN protocol](../policyapi/README.md#pin-verifier-protocol).

## Run and use

```powershell
dotnet run --project src/managementportal
```

Open `https://localhost:7080/`, click **Log in**, and sign in through the configured identity provider. An authorized user lands on the overview. **Policies** opens policy administration; **My Account** displays read-only profile claims. Users without `platform-admin` receive HTTP 403.

Create fingerprint, PIN, fingerprint-or-PIN or deny-all policies. Optional restrictions cover even/odd minutes and a same-day daily window, with inclusive start and exclusive end. At most one policy is active. Creating or selecting an active policy deactivates the previous one; **Pause all access** leaves none active. Removing the active policy also pauses access. PIN changes and activation changes increment affected policy versions.

The development launch profile binds browser HTTPS at `https://localhost:7080` and anonymous device HTTP at `http://0.0.0.0:5080`. Browser HTTP redirects to HTTPS. Use a trusted private bench LAN and allow port 5080 only where needed. From a UNO, use the computer's LAN address rather than `localhost`. The HTTPS [E2E_remote sketch](../../hardware/arduino/E2E_remote/README.md) targets the hosted application.

```powershell
curl.exe --header "Accept: application/json" http://localhost:5080/api/policy/active
```

This returns the active policy or HTTP 204 when access is paused. Devices must deny access when no valid policy is available. Time restrictions are evaluated by the device; E2E_remote obtains UTC over HTTPS and evaluates policies in Europe/London time.

## Security boundaries

This is a bench proof of concept. All device API reads are anonymous, including inactive policies and PIN verifiers. Anyone who can reach the API can read policy IDs, schedules and verifiers. Use demonstration data only. HTTPS protects hosted transport but does not authorize a device; the shared PIN key does not authenticate API requests or sign other policy fields. The local HTTP variant is vulnerable to policy tampering and replay.

The shared key is a software credential on the app and UNO. Someone who obtains it and a verifier can guess short PINs offline. A salted verifier does not make the board a secure credential store. See the [project limitations](../../README.md) before adapting this for real access control.

Portal forms, login and logout use antiforgery protection. Session cookies are Secure, HttpOnly and expire after 30 minutes without sliding renewal. The ID token is retained only for the logout hint; access and refresh tokens are discarded. Sign out clears the portal cookie and Keycloak session, while a LinkedIn browser session may remain active. There is no back-channel logout or continuous role revalidation: a local session can retain permissions until sign-out or expiry after an upstream role/session change.

## Storage and compatibility

`PolicyStore__Provider` selects `File` (default) or `AzureBlob`, ignoring case. Invalid or incomplete settings fail startup. File storage uses `App_Data/policies.json`, excluded from Git and publishing; `PolicyStore__FilePath` overrides it. Use one process with File storage. On App Service, the default file location is temporary, so select AzureBlob for durable policies.

Both stores validate the same JSON document and maintain one active policy. Legacy four-field records load as inactive fingerprint-only policies, preserving IDs and minute rules. Reading does not rewrite them; the next change persists the migration. Corrupt records, duplicate IDs or multiple active schema-2 policies fail closed.

### Azure Blob configuration

| Environment setting | Value |
| --- | --- |
| `PolicyStore__Provider` | `AzureBlob` |
| `PolicyStore__BlobServiceUri` | `https://<account>.blob.core.windows.net/` |
| `PolicyStore__ContainerName` | `policies` |
| `PolicyStore__BlobName` | `policies.json` |

The service URI must be an HTTPS account root without credentials, path, query or fragment. Provision a private container and grant the app's system-assigned managed identity **Storage Blob Data Contributor** at container scope. Development uses `DefaultAzureCredential` with equivalent data access. No storage account key or connection string is needed.

The app does not create the container. A missing blob in an accessible container starts empty and is created on the first change. Missing containers, access failures, network failures and corrupt documents return errors; API reads return HTTP 503. There is no local-file fallback.

Keep the OIDC secret and existing device key in Key Vault, supplied through App Service references:

```text
Authentication__ClientSecret = @Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<portal-client-secret>)
PolicySecurity__SharedKeyHex = @Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/<device-shared-key>)
```

Grant the app identity permission to read those secrets. Preserve the device key when moving storage. Pause editing, securely back up and upload the existing UTF-8 policy document to the private blob, configure the app, then verify policy IDs, activation and device PIN behavior before resuming edits.

Blob writes use ETag conditions and recompute after conflicts, with at most eight attempts and SDK retries disabled. A lost response after a successful write can leave its outcome uncertain; reload before retrying. Blob concurrency alone does not establish multi-instance portal/session readiness.

## App Service ingress and health

The [hosting runbook](../../infrastructure/managementportal/README.md) and [deployment workflow](../../.github/workflows/deploy-managementportal.yml) cover Azure hosting. Hosted application/API requests require HTTPS. Anonymous health GET/HEAD responses have no body and send `Cache-Control: no-store`:

| Endpoint | Behavior |
| --- | --- |
| `/health/live` | HTTP 200 without dependency access; Front Door probe |
| `/health/ready` | HTTP 200 when policy storage is readable, otherwise HTTP 503; App Service health check |

Readiness has a two-second limit. Configure `Hosting__Enabled`, the canonical public hostname and exact Front Door profile GUID. App Service network restrictions must allow only `AzureFrontDoor.Backend` with that `X-Azure-FDID` and deny unmatched main-site traffic. Keep separate SCM rules for deployment. The application header check is defense in depth and depends on those network restrictions.

`Hosting__TrustAppServiceProxy=true` requires App Service and those restrictions. Other hosts can specify narrow `Hosting__KnownProxies__0` or `Hosting__KnownIPNetworks__0` values. Keep `ASPNETCORE_FORWARDEDHEADERS_ENABLED=false`; forwarding is handled explicitly, allows the canonical hostname and processes at most two hops. Set `AllowedHosts` to the public and actual App Service hostnames. Verify session behavior across restart; deployment slots and external session-key storage need separate validation.

## Build, check and publish

```powershell
dotnet build src/managementportal
dotnet run --project tests/policyapi
dotnet run --project tests/managementportal
dotnet publish src/managementportal -c Release -o build/managementportal
```

The console harnesses use isolated stores, simulated Blob responses and a fake OIDC provider. They cover policy/PIN rules, concurrent writes, login/logout, token validation, roles, antiforgery, profile encoding, storage failures, trusted forwarding and health checks. They do not contact Keycloak, LinkedIn or Azure and do not prove live identity, RBAC, network or hardware configuration. The portal runner's optional `--export-preview` argument writes rendered pages under ignored `build/portal-preview`.

The published directory contains the portal and referenced policy library. The Linux startup command is `dotnet ManagementPortal.dll`. For live acceptance, verify browser login/logout, role denial, readiness, policy persistence across restart and device behavior. Planned device lifecycle and audit work is tracked in the [roadmap](../../docs/roadmap.md).
