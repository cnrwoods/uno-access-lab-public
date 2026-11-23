# Fing Auth login theme

The `fingauth` theme inherits `keycloak.v2`. It adds the portal's colours and
fingerprint logo, keeps the **Provided by Keycloak** footer, and inherits native
login, social-provider, validation and required-action templates.

## Build the image

From `infrastructure/keycloak`, using local `config/config.ps1`:

```powershell
.\scripts\04-build-keycloak-image.ps1 -ImageTag '26.8.2-fingauth-v1'
```

Use a new versioned tag for each build. `ImageTag` is a Docker tag, not a full
image URL. The build context allowlist excludes local config and credentials.
The helper checks both Containerfile stages match `$KeycloakVersion` before
submitting a build. Building does not change the running server.

## Upgrade the existing server

Changing an older engine to the repository's **26.8.2** target needs a maintenance
upgrade. Check the running version in **Admin Console > Server info** and review
the [release upgrade guide](https://www.keycloak.org/docs/26.8.2/upgrading/)
before proceeding. Do not use step 13 for an engine upgrade.

Stop every old instance before starting a different engine. Container Apps'
single revision mode can overlap old and new replicas during startup. Save the
configuration and old image reference, verify the automatic database recovery
window, and test restoring to a separate server first. A migrated database may
require the old database restored together with the old server; changing only
the image is insufficient.

The lab's Burstable PostgreSQL tier has no on-demand backups. A point-in-time
restore creates a separate billable server. Use the original private subnet and
DNS zone, wait for **Ready**, then check it from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File infrastructure/keycloak/scripts/14-test-keycloak-restore.ps1 -RestoredServerName 'fingauth-restore-check'
```

Step 14 creates a temporary manual job in the existing private environment. It
uses managed identity and existing Key Vault references, runs read-only SQL
against the separate restored server and removes the job afterward. It checks
the realms, portal client, enabled LinkedIn provider, admin role and presence of
application users. Override `-LinkedInAlias` if it differs from
`linkedin-openid-connect`. The running app is unchanged. These checks do not
establish a complete data match or working browser login.

A rollback also needs Keycloak pointed at the restored database. Keep the
restored server until maintenance verification passes, then remove it when no
longer needed. After an upgrade, test discovery, administrator/MFA access,
password login, LinkedIn login and portal authorization. Do not rerun initial
deployment step 06 after bootstrap cleanup.

## Later theme updates

For an image using the **same verified engine version**, preview then roll out:

```powershell
.\scripts\13-update-keycloak-image.ps1 -ImageTag '26.8.2-fingauth-v1' -RunningKeycloakVersion '26.8.2' -WhatIf
.\scripts\13-update-keycloak-image.ps1 -ImageTag '26.8.2-fingauth-v1' -RunningKeycloakVersion '26.8.2'
```

The helper requires the supplied running version to match config; it cannot
independently verify the engine. It resolves the tag to a digest, changes only
the existing container's image and waits for the new revision's health. The
preview performs read-only lookups. Keep the printed previous-image command
until verification passes; it is usable only with a compatible database.

## Select and verify

In the application realm, set Display name to **Fing Auth** and choose
**fingauth** under **Realm settings > Themes > Login theme**. Keep the internal
realm name unchanged. Start a fresh portal login in a private browser window
to avoid an existing session bypassing the screen.

Check username/password and LinkedIn sign-in, branding, keyboard focus, error
feedback, required actions and the provider footer. Confirm successful login
returns to the portal and the permitted account can manage policies. If the
theme is missing, check the active image contains
`themes/fingauth/login/theme.properties`.

Review CSS, messages and the footer against the parent theme on every engine
upgrade. Selecting the previous login theme reverses presentation changes; it
does not undo a server/database migration. See
[Keycloak theme inheritance](https://www.keycloak.org/ui-customization/themes).
