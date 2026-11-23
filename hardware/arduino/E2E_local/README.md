# E2E_local fingerprint/PIN and solenoid bench sketch

The LAN/HTTP bench sketch combines fingerprint/PIN policy checks with physical unlocking. See [E2E_remote](../E2E_remote/README.md) for HTTPS and internet clock synchronisation, and the [hardware guide](../../README.md) for wiring.

## Configure and compile

Hardware: Arduino UNO R4 WiFi, ID809 I2C fingerprint sensor, existing keypad wiring, Adafruit ADA5648 MOSFET driver and dedicated 12 V / 2 A solenoid supply. MOSFET `IN` uses **D9**, active HIGH, with common Arduino/driver ground. Solenoid red connects to driver `+`, blue to `−`. Keypad rows remain `{8,7,6,5}` and columns `{4,3,2}`. Serial Monitor remains **9600 baud**.

Copy `arduino_secrets.example.h` to the Git-ignored `arduino_secrets.h`. Configure Wi-Fi, the computer's numeric LAN `POLICY_HOST`, `POLICY_PORT` and the matching portal `SHARED_KEY_HEX` there. See [portal PIN setup](../../../src/managementportal/README.md#pin-setup) for provisioning the server key; its helper creates a header for the earlier sketch, so copy that same private key into this sketch's local header. Never commit or print credentials.

This sketch uses a manually trusted clock. Send `TIME HH:MM` with a newline after each reboot, or `TIME CLEAR` to invalidate it. There is no timezone conversion. A full-day, any-minute policy does not require a valid clock; time-restricted policies do. The active-policy endpoint is polled every 10 seconds, with a 30-second cached-policy lifetime. The API is anonymous; keep HTTP testing on an isolated, trusted LAN.

Required libraries: DFRobot_ID809_I2C, Adafruit Keypad, ArduinoJson 7 and Crypto by Rhys Weatherley; WiFiS3, RTC and Arduino_LED_Matrix come with the UNO R4 board package. With `arduino-cli` on PATH, run from the repository root:

```powershell
arduino-cli compile --fqbn arduino:renesas_uno:unor4wifi hardware/arduino/E2E_local
```

Compilation passed with UNO R4 core **1.6.0**: **92,440 bytes flash**, **11,356 bytes RAM**. Compilation did not upload firmware or exercise the hardware.

## Actuator behaviour

- **Modular actuator:** `SolenoidController.h` provides `initialiseSolenoid()`, `unlockSolenoid()`, `lockSolenoid()`, `serviceSolenoid()` and an active-state query. The first action in `setup()` writes D9 LOW before configuring OUTPUT. A pulse uses unsigned `millis()` subtraction for a 2,000 ms duration, including rollover handling. A PERMIT while active leaves the original deadline intact. No blocking delay was added for actuation.
- **Shared validated decision:** the existing PIN and fingerprint `allowed` booleans still feed `showResult()`. Only its final successful branch calls `unlockSolenoid()`. There is no remote `PERMIT` string or serial unlock command. Fingerprint match or correct PIN alone cannot unlock: existing policy/schema/freshness, schedule, method, HMAC and PIN safeguards still apply.
- **Final connectivity checks:** `scheduleAllows()` additionally requires live Wi-Fi connectivity. `showResult()` rechecks this schedule/policy gate before success feedback and after synchronous sensor LED commands. Unlock starts after feedback commands finish; a decision invalidated during feedback becomes DENY.
- **Feedback preserved:** the existing bitmap and sensor LED rendering was extracted into `renderAccessFeedback()` without changing tick, cross, neutral keypad bitmap or LED commands. Existing result duration and serial diagnostics remain. No PIN or authentication secret logging was added.
- **Cancellation and servicing:** policy invalidation and DENY now immediately lower D9 before feedback operations. The loop services pulse expiry and active-policy/connectivity checks around existing services; serial processing services expiry and rechecks safety after clock commands. Detected policy expiry, invalid schedule or Wi-Fi loss cancels an active pulse.
- **Blocking-work mitigation:** policy polling is deferred while a pulse is active, without advancing its poll timestamp. `fetchPolicy()` also defensively locks before any HTTP work. Fingerprint polling is deferred while active. This can postpone a due policy poll by approximately two seconds in normal operation. HTTP parsing, bounds, deadlines and fail-closed error handling are otherwise unchanged.

PIN length/overflow handling, HMAC-SHA256 verification, five-attempt/30-second PIN lockout, fingerprint matching and lift-to-retry behavior remain. The base sketch has no enrolment or template-deletion commands; existing separate enrolment sketches and stored templates are untouched.

## Timing and hardware limits

The retained HTTP implementation can block for seconds (10-second transaction deadline and 3-second configured TCP connection timeout), so it runs with D9 LOW. Other synchronous Wi-Fi/RTC calls and keypad-triggered fingerprint LED commands can still delay cooperative timer servicing. The installed DFRobot library also has an unbounded I2C-ready wait. **A strict two-second maximum is not guaranteed by this cooperative software.** Use an independent, non-retriggerable hardware cutoff for a guaranteed maximum pulse.

API availability is known only when polled: a failed request invalidates policy and cannot cause unlocking, but cached valid policy can authorize between polls before an outage is detected. Active-pulse polling deferral also delays detecting remote changes. Wi-Fi loss and policy invalidity cancel the pulse when detected; neither detection nor servicing is guaranteed during a blocked driver call.

This tabletop PoC omits the recommended **10 kΩ MOSFET input pull-down**. Software initialization cannot guarantee inactivity during reset or a floating GPIO state. Add that resistor and an independent cutoff before further reliability work. This configuration is **not hardware fail-safe or suitable for production access control**.

## Physical acceptance checks — pending

Compilation and source review do not establish the following physical results. Run these checks after uploading the E2E sketch:

| Scenario | Expected result |
|---|---|
| Fingerprint + validated PERMIT | Existing tick/green feedback; D9 HIGH for nominally two seconds, then LOW |
| Fingerprint + DENY | Existing cross/red feedback; D9 LOW |
| PIN + validated PERMIT | Existing success feedback; nominal two-second unlock |
| Invalid PIN or active PIN lockout | Existing failure/lockout behavior; D9 LOW |
| Disallowed authentication method | D9 LOW |
| Outside schedule, invalid clock when required | D9 LOW; active pulse cancelled when detected |
| Invalid/stale policy or failed API poll | No new unlock; active pulse cancelled on invalidation |
| API unavailable between polls | Cached-policy limitation above applies until detected |
| Wi-Fi disconnected | No new unlock; active pulse cancelled when detected |
| Startup | D9 latch LOW before OUTPUT; reset/floating limitation still applies |
| Timer expiry | D9 LOW on the next timer service; measure actual pulse duration |
| Repeated PERMIT during active pulse | Original pulse deadline remains unchanged |
| Matrix/keypad/sensor feedback | Existing tick, cross, neutral bitmap and sensor LED states remain |

Also measure D9 under delayed HTTP responses, Wi-Fi loss, clock invalidation and sensor communication faults. Do not infer a guaranteed cutoff from normal-operation measurements.
