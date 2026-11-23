# E2E_remote: HTTPS policies and internet time

UNO R4 WiFi fingerprint/PIN and solenoid bench sketch for the live Azure
deployment. The working LAN/HTTP sketch is preserved as
[E2E_local](../E2E_local/README.md). Fingerprint matching, PIN verification,
policy evaluation and the existing nominal two-second D9 pulse remain local.

## Configuration

The default policy endpoint is:

```text
https://fingauth.conorwoods.co.uk/api/policy/active
```

`WiFiSSLClient` connects using the DNS hostname and the connectivity firmware's
trusted CA bundle. There is no plaintext fallback, certificate-verification
bypass, redirect following or leaf-certificate pinning. The policy JSON schema,
PIN HMAC verification, active-policy HTTP 204 behavior, 10-second polling and
30-second cached-policy validity are retained from E2E_local.

Copy `arduino_secrets.example.h` to `arduino_secrets.h` and configure Wi-Fi
locally. Set `SHARED_KEY_HEX` to the same private key used by the portal; keep
that key stable when switching from E2E_local so existing PIN verifiers remain
valid. Both sketches' real headers remain Git-ignored; never commit or log
credentials. See [portal PIN setup](../../../src/managementportal/README.md#pin-setup)
for provisioning the server key.

```cpp
#define POLICY_HOST "fingauth.conorwoods.co.uk"
#define POLICY_PORT 443
#define TIME_API_HOST "timeapi.io"
#define TIME_API_PATH "/api/time/current/zone?timeZone=UTC"
```

The policy API remains intentionally anonymous for this PoC. Authenticating and
authorising individual devices is future device-registration work. HTTPS
authenticates the server and protects transport; the shared PIN key does not
authenticate requests to the API.

## Clock behavior

After Wi-Fi connects at boot, the sketch requests UTC from the certificate-
validated HTTPS [TimeAPI endpoint](https://timeapi.io/api/time/current/zone?timeZone=UTC).
It requires HTTP 200, numeric calendar fields, a valid date in 2024–2099,
`timeZone: "UTC"` and `dstActive: false` before setting the RTC. Existing RTC
contents are not trusted after a reboot without a new successful network sync.
The connectivity firmware's own TLS clock is separate from this RTC and is
normally synchronised by that firmware over NTP.

The RTC stores **UTC**. Policy daily windows and even/odd minutes use
**Europe/London** wall time, including British Summer Time. Conversion follows
the current UK rule: the last Sunday in March/October at 01:00 UTC. The autumn
repeated hour is evaluated twice; the spring skipped hour does not occur. These
are daily wall-clock policies, not one-time appointments.

- Until a valid network clock is available, **all access is denied**, including
  policies with an unrestricted daily window.
- Failed boot syncs are retried once a minute while connected. Wi-Fi recovery
  also requests a time refresh; it does not depend on a valid clock.
- A valid clock is refreshed every six hours and expires after 24 hours without
  another successful sync. A failed refresh retains an unexpired clock.
- RTC readings must progress plausibly against `millis()` (maximum discrepancy
  120 seconds); invalid/jumped readings deny access and invalidate the clock.
  Network delay and RTC drift still introduce timing uncertainty.
- Successful clock correction invalidates the cached policy and requires a
  fresh policy download; time requests do not renew policy age.
- Both time and policy network operations run with D9 LOW and are deferred while
  an unlock pulse is active.

Serial Monitor remains **9600 baud**, using newline-terminated commands:

| Command | Result |
| --- | --- |
| `TIME STATUS` | Show UTC and Europe/London time, or report an invalid clock |
| `TIME SYNC` | Clear clock/policy validity and request a new HTTPS time sync |
| `TIME CLEAR` | Clear clock/policy validity; automatic retry resumes after a minute |

Manual `TIME HH:MM` is supported by E2E_local only. Remote time recovery is not a
permanent pause control; use the portal's pause-all-access policy control for that.

## Build and validation

Required libraries are the same as E2E_local: DFRobot_ID809_I2C, Adafruit Keypad,
ArduinoJson 7 and Crypto by Rhys Weatherley. WiFiS3/WiFiSSLClient, RTC and
Arduino_LED_Matrix come with the UNO R4 board package. The preceding standalone
HTTPS diagnostic passed on board package 1.6.0 / connectivity firmware 0.6.0.

```powershell
arduino-cli compile --fqbn arduino:renesas_uno:unor4wifi hardware/arduino/E2E_remote
```

Both TLS handshakes and HTTP responses have separately configured 10-second
wait limits. HTTP reads are bounded to 1024 bytes per header line, 4096 header
bytes, 8192 wire bytes and 2048 body bytes. Compression, ambiguous framing,
unsupported chunk extensions/trailers, incomplete bodies and malformed policies
are rejected. Synchronous modem/driver calls can extend configured waits; this
is not a hard real-time cutoff.

Compile-time checks for real calendar boundaries, UK DST, clock expiry and
`millis()` rollover are in [tests/arduino/remote_clock](../../../tests/arduino/remote_clock/README.md).
See `validation.md` for build evidence, operator-reported bench checks and
the fault-injection/endurance regression checklist.

The original actuator wiring and hardware limitations described in
[E2E_local](../E2E_local/README.md) still apply, including the omitted MOSFET
pull-down and lack of an independent pulse cutoff. Disconnect the solenoid's
12 V supply before uploading/testing a new firmware or wiring change. Repeat
network, recovery and actuator checks for those changes; compilation alone does
not establish hardware behavior. On 22 November 2026, bench checks confirmed
remote hardware operation, including active-policy switching, PIN changes and
even-minute schedules against the live cloud application.
