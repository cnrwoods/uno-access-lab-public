# Remote sketch validation

## Recorded builds: 21 November 2026

UNO R4 board package **1.6.0**:

| Sketch | Flash | Global RAM | Result |
| --- | ---: | ---: | --- |
| E2E_local | 92,440 bytes (35%) | 11,356 bytes (34%) | Passed |
| E2E_remote | 99,356 bytes (37%) | 11,380 bytes (34%) | Passed |
| Remote clock assertions | 51,880 bytes (19%) | 6,740 bytes (20%) | Passed |

The local sketch was preserved from the working LAN baseline. The clock
assertions use `RemoteClock.h` and cover leap dates, invalid dates, RTC year
bounds, UTC epoch values, UK daylight saving transitions, midnight conversion,
expiry, timer rollover and stopped/jumped clocks. They require compilation only.

A computer-side HTTPS request verified the TimeAPI UTC response schema. Source
review checked bounded HTTP reads, separate TLS/HTTP deadlines, deferred network
work during unlock pulses, independent clock expiry, and invalidation of cached
permissions after clock correction. Computer connectivity and source review
alone do not demonstrate these behaviours on a UNO.

## Operator-reported bench checks: 22 November 2026

The remote sketch worked against the deployed Azure portal, including:

- Switching active policies in the cloud portal.
- Changing PINs and using the changed policy on the device.
- Even-minutes-only access scheduling.

These were operator-reported checks, not an instrumented endurance run. The
[portal hosting guide](../../../infrastructure/managementportal/README.md)
describes the deployment. The separate
[HTTPS diagnostic record](../https_test/validation.md) covers TLS transport and
certificate-validation behaviour.

## Outstanding fault and endurance checks

The scenarios below remain a regression checklist. Disconnect the solenoid's
12 V supply for initial network checks.

| Scenario | Required result |
| --- | --- |
| Boot with working Wi-Fi/time service | Valid UTC RTC; correct Europe/London status; fresh policy before access |
| Time service/TLS/schema failure at boot | No access; bounded failure and retry after a minute |
| Failed six-hour time refresh | Retain unexpired clock; deny after 24-hour clock age |
| Wi-Fi reconnect | No access while disconnected; refresh time and policy after reconnect |
| `TIME CLEAR` / `TIME SYNC` | Invalidate clock and policy; require successful sync and fresh policy |
| Policy HTTP 200 / 204 | Validate active policy / deny when paused |
| Policy TLS, HTTP or schema failure | Invalidate policy and deny |
| Valid credential and schedule | Expected feedback and nominal two-second pulse |
| Expired clock/policy, Wi-Fi loss or DENY | No new pulse; cancel active pulse when detected |
| HTTPS while pulsing | Defer network work until the pulse ends |
