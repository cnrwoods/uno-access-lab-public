# HTTPS transport validation

Recorded on **21 November 2026**.

## Setup

- Arduino UNO R4 WiFi on COM5; board package `arduino:renesas_uno` **1.6.0**.
- Connectivity firmware **0.6.0**; no firmware update was required.
- The operator confirmed the solenoid's 12 V supply was disconnected before upload and
  remained disconnected for the physical USB power-cycle check.
- Existing ignored diagnostic Wi-Fi credentials were used without logging them.
- Original E2E source and local settings were left unchanged.

The public endpoint's certificate chain was independently checked using a
hostname-validated .NET TLS connection:

```text
idp.fingauth.conorwoods.co.uk
  -> GeoTrust TLS RSA CA G1
  -> DigiCert Global Root G2
```

The ISRG Root X1 certificate embedded in `UnrelatedTestRoot.h` is a valid public
root deliberately unrelated to that chain. Its SHA-1 thumbprint is
`CABD2A79A1076A31F21D253635CB039D4329A5E8`.

## Compile and device results

The diagnostic compiled successfully with all warnings enabled:

- Flash: **68,736 / 262,144 bytes (26%)**.
- Global RAM: **7,588 / 32,768 bytes (23%)**.
- Compiler warnings were in the installed Arduino core/WiFiS3 sources; none
  originated from the diagnostic sketch.
- Upload to COM5 succeeded.

All three observed suites returned `SUITE: PASS`:

| Run | Default-bundle TLS | Unrelated CA rejected | Restored-bundle TLS | Discovery checks |
| --- | ---: | ---: | ---: | --- |
| Initial observed run | 601 ms | 10,190 ms | 578 ms | HTTP 200 and exact issuer, twice |
| Wi-Fi disconnect/reconnect | 1,055 ms | 10,190 ms | 596 ms | HTTP 200 and exact issuer, twice |
| Physical USB cold start | 631 ms | 10,190 ms | 606 ms | HTTP 200 and exact issuer, twice |

For reconnect, the monitor observed `Wi-Fi connected: no` after `D`, followed by
a successful join and full suite after `R`. For cold start, the operator unplugged
and reconnected USB, and the monitor observed COM5 disappear and reappear. It
captured the automatic startup suite **without sending `R`**.

The negative TLS connection returned failure at the configured connection wait
limit; WiFiS3 did not expose a detailed TLS failure reason. Successful controls
immediately before and after, against the same hostname and network, support
the trust-rejection result. This check is not separate evidence of hostname
mismatch, certificate expiry or revocation enforcement. Physical D9 voltage and
actuator timing were not measured; the diagnostic commands D9 LOW and the 12 V
supply was disconnected.

The local raw diagnostic capture is at `build/https-test-serial.log`, which is
Git-ignored. This record contains its relevant results without credentials.

## E2E restoration

Before the diagnostic upload, the unchanged E2E sketch was compiled into a
separate restoration directory:

- Flash: **92,440 bytes**; global RAM: **11,356 bytes**.
- Restoration upload to COM5 succeeded after the cold-start test.
- At 9600 baud, E2E acknowledged `TIME CLEAR` with `Test clock invalidated.`
- The solenoid supply stayed disconnected during this diagnostic session.

Time-restricted policies require the usual manual `TIME HH:MM` after this reboot.
No PIN/fingerprint/actuator acceptance tests were performed during this
networking diagnostic.

## Outcome

The UNO can use DNS and the built-in CA bundle to reach the existing Azure Front
Door over HTTPS. CA rejection, Wi-Fi reconnection and a physical cold start
passed. The working local E2E firmware was restored after this session. HTTPS
integration and cloud policy checks are now recorded separately in
[E2E_remote validation](../E2E_remote/validation.md).

The original E2E source was subsequently renamed to `E2E_local/E2E_local.ino`
without changing its contents. Restoration instructions now use that path;
the results above describe the original diagnostic run before the rename.
