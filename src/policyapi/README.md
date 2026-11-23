# Policy API and UNO contract

The management portal hosts this .NET 10 library. Local development exposes the device API on port 5080; the hosted deployment uses HTTPS. Run `dotnet run --project src/managementportal` after completing the [portal setup](../managementportal/README.md), then create policies at `/Policies`.

## Endpoints

| Request | Response |
| --- | --- |
| `GET /api/policies` | All policies as a JSON array; empty store is `[]`. |
| `GET /api/policy` | Alias for the same list. |
| `GET /api/policy/active` | One active policy object, or HTTP 204 with no body when none is active. Preferred UNO endpoint. |
| `GET /api/policies/{policyId}` | One policy object, or HTTP 404. ID lookup ignores case. |

Responses use `Cache-Control: no-store`; JSON uses `application/json`. Read failures return 503, never a misleading empty policy. API mutations are not exposed; portal forms create, activate, pause and reset PINs with antiforgery protection. These contracts are identical for File and AzureBlob storage.

**All read endpoints are anonymous**, including the complete list of inactive policies. Anyone who can reach the API can read policy IDs, schedules and salted PIN verifiers. Clear-text PINs and the shared registration key are never returned. Use demonstration data only; device authentication and authorization are required before this design is suitable for a real access-control deployment.

`enabled` now means **the active policy**. At most one policy can have `enabled: true`. An active `denyAll` policy explicitly denies all access. No active policy must also deny access. Versions start at 1 and increase for changed records when activation or PIN updates occur.

## Schema 2

This example uses the **public test vector**, not the secret generated for your actual local registration:

```json
{
  "policyId": "POL-001",
  "version": 1,
  "enabled": true,
  "allowedMinuteParity": "any",
  "schemaVersion": 2,
  "accessMode": "pinOnly",
  "startMinute": 0,
  "endMinute": 1440,
  "pin": {
    "algorithm": "HMAC-SHA256",
    "salt": "000102030405060708090a0b0c0d0e0f",
    "hash": "ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f",
    "keyId": "630dcd2966c43366"
  }
}
```

- `policyId`: 1-64 ASCII letters, digits, hyphens or underscores; starts with a letter or digit. Portal saves uppercase IDs.
- `schemaVersion`: exactly `2`; `version`: positive integer revision of this policy.
- `accessMode`: `fingerprintOrPin`, `pinOnly`, `fingerprintOnly`, or `denyAll`.
- `allowedMinuteParity`: `any`, `even` or `odd`, evaluated against the current minute (0-59).
- `startMinute` / `endMinute`: same-day minutes since midnight, inclusive start and exclusive end. `0 <= startMinute < endMinute <= 1440`; no overnight windows. Full day is 0-1440.
- `pin`: protected credential required for PIN modes, `null` for fingerprint-only and deny-all modes.

All applicable conditions must pass. Fingerprint or PIN means either successful method is sufficient; it does not mean two-factor authentication. Time-restricted policies require a valid device clock. These comparisons and HMAC verification fit the UNO R4's normal sketch execution using its RTC and a SHA-256 library. See [Arduino's UNO R4 specifications](https://docs.arduino.cc/resources/datasheets/ABX00087-datasheet.pdf) and [the Crypto library HMAC API](https://rweather.github.io/arduinolibs/classSHA256.html).

## PIN verifier protocol

Registration provisions an identical random **32-byte secret** to the app and the UNO. It is never part of the policy. The app accepts a PIN as 4-8 ASCII digits, preserving leading zeroes, and chooses a fresh random 16-byte salt each time it is set.

Compute HMAC-SHA256 using the decoded 32-byte secret as the key. The message is exactly the UTF-8 bytes of:

```text
uno-access-lab/pin/v1\n{policyId}\n{salt}\n{enteredPin}
```

Here `\n` means one LF byte, not a literal backslash and letter. Use the ID's exact stored case and the salt's 32 lowercase hexadecimal characters as **text**, not decoded bytes. There is no trailing newline or NUL. Encode the 32-byte result as 64 lowercase hex characters in `hash`.

`keyId` is the first 16 lowercase hexadecimal characters of SHA-256 of the decoded registration key. It identifies a key mismatch; it is not a key or an authentication token. Compare decoded verifier bytes in constant time. Reject unsupported algorithms, wrong lengths/types, unknown schemas or a mismatched key ID.

The [public interoperability vector](#public-pin-interoperability-vector) is checked by the .NET regression tests and the Arduino sketch's boot self-test. The sketch can check a typed PIN without ever receiving a recoverable PIN value from the server.

This is a keyed, salted verifier, **not encrypted PIN storage or a signature over the policy**. Anyone with the shared secret can guess short PINs offline from the verifier. HTTP policy traffic is suitable only for the trusted local PoC; use validated TLS and authenticated administration for deployment. Enter PINs into the portal over localhost or HTTPS.

## Try the UNO

Follow [portal PIN provisioning](../managementportal/README.md#pin-setup) and the [E2E_remote guide](../../hardware/arduino/E2E_remote/README.md) for HTTPS policies, network time and solenoid control. [E2E_local](../../hardware/arduino/E2E_local/README.md) provides the LAN/HTTP version. Both fetch `/api/policy/active` every 10 seconds and deny access when a valid policy is unavailable; their cached policy expires after 30 seconds. Earlier fixed-PIN and single-object experiments are not schema-2 clients.

Old four-field policy documents migrate to inactive fingerprint-only entries. The standalone app on port 5081 remains retired; publish and run the management portal for both UI and API.

## Storage providers

`FilePolicyStore` remains the default, with the existing local JSON path and one-process coordination. `BlobPolicyStore` stores the complete JSON list in one private blob. Both use shared policy validation, legacy migration and version/activation rules through `JsonPolicyStore` and `PolicyDocument`.

The portal selects Blob storage through these environment variables:

```text
PolicyStore__Provider=AzureBlob
PolicyStore__BlobServiceUri=https://<account>.blob.core.windows.net/
PolicyStore__ContainerName=policies
PolicyStore__BlobName=policies.json
```

Production authenticates with the App Service system-assigned managed identity and container-scoped **Storage Blob Data Contributor**. There is no storage secret. The private container is provisioned separately; the app does not create it or fall back to a local file. Missing blobs initialize empty, while missing containers and access/network failures return storage errors. See the [portal configuration and migration procedure](../managementportal/README.md#azure-blob-configuration), including the existing shared-key Key Vault reference.

Blob mutations use `If-Match` for an existing ETag or `If-None-Match: *` for a new document. Conflicts re-read the latest policies and recompute the change, with at most eight write attempts. This protects concurrent writers from lost updates and preserves the single-active-policy rule. SDK network retries are disabled; an ambiguous connection failure is reported rather than replaying a write. This is not an exactly-once delivery guarantee.

Changing storage does not change device PIN credentials. Migrate the existing UTF-8 policy document during a pause in editing, keeping the same IDs, versions and shared key. Validate identity permissions and migration against your own deployment. Blob concurrency support alone does not establish multi-instance portal/session readiness.

## Regression checks

Run `dotnet run --project tests/policyapi` from the repository root. The console harness uses temporary policy files and simulated Blob HTTP responses; a failed check produces a nonzero exit code. It covers validation, migration, persistence, activation, PIN replacement, corruption, cancellation and concurrent writes. It uses public test credentials and does not contact Azure or an Arduino. Run `dotnet run --project tests/managementportal` as well for endpoint, authentication and storage-configuration checks.

## Public PIN interoperability vector

These are public test values. Never use this key for a deployed application or device.

| Input | Value |
| --- | --- |
| Registration key (32 decoded bytes, hex) | `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f` |
| Policy ID | `POL-001` |
| PIN (text) | `0007` |
| Salt (lowercase hex text) | `000102030405060708090a0b0c0d0e0f` |
| Algorithm | `HMAC-SHA256` |
| Expected hash | `ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f` |
| Expected key ID | `630dcd2966c43366` |

The HMAC input contains these four lines, separated by one LF byte, with no trailing newline or NUL:

```text
uno-access-lab/pin/v1
POL-001
000102030405060708090a0b0c0d0e0f
0007
```

Use the salt as hexadecimal text and the key as decoded bytes. Policy ID case is significant to the verifier.
