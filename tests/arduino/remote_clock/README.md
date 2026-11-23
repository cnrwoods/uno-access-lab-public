# Remote clock checks

This sketch runs compile-time assertions against the actual
`hardware/arduino/E2E_remote/RemoteClock.h` implementation. It needs no secrets,
network access or device upload.

Validated on 21 November 2026 with UNO R4 board package 1.6.0: compilation
passed all assertions (51,880 bytes flash, 6,740 bytes global RAM). No upload
was performed.

```powershell
arduino-cli compile --fqbn arduino:renesas_uno:unor4wifi tests/arduino/remote_clock
```

Coverage includes malformed calendar ranges, leap dates, supported RTC years,
independently calculated Unix timestamps, Sunday/Monday numbering, the UK
2026/2027/2028 clock-change dates, exact spring/autumn transition seconds,
midnight wrapping, clock expiry, `millis()` rollover, explicit clock clearing,
and detecting a stopped or jumped RTC. UK transition dates are checked against
[GOV.UK](https://www.gov.uk/when-do-the-clocks-change); UTC epoch values were
independently calculated using .NET `DateTimeOffset`.

The header deliberately supports 2024-2099, uses the current recurring UK DST
rule, and does not claim historic timezone correctness. Compilation verifies
the portable logic, not the RTC hardware, time service or TLS/network path.
