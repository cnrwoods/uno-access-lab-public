# ADR-0002: Use the SEN0359 fingerprint kit

**Accepted:** 27 September 2026. **Owner:** Conor Woods.

## Decision

Use the DFRobot Gravity SEN0359 reader with its supplied adapter and cables.
Connect 5 V, GND, SDA and SCL to the UNO's main headers using `Wire`. This replaces
the earlier Adafruit reader, UART level converter, separate regulator and
soldered-header arrangement. The UNO R4 WiFi decision remains unchanged.

The assembled adapter and plug-in leads reduce assembly work for the first
sensor exercise. Matching and template storage remain on the reader. The
SEN0348 bare sensor is not a direct substitute for the complete kit.

## Consequences

- Use DFRobot_ID809_I2C. Inspect connector labels with power disconnected; the
  UNO's separate Qwiic connector/`Wire1` is not used by these sketches.
- I2C match messages are unauthenticated. The adapter does not protect against
  physical tampering or forged responses.
- Enrolment and deletion must be supervised. Keep biometric templates out of
  source control and distinguish communication errors from successful matches.

Reader matching is now integrated into the end-to-end bench sketches. See the
[hardware guide](../../../hardware/README.md) and
[remote validation record](../../../hardware/arduino/E2E_remote/validation.md)
for implementation and recorded checks.

## References

- [Manufacturer kit specification](https://www.dfrobot.com/product-2165.html)
- [SEN0359 wiring and examples](https://wiki.dfrobot.com/sen0359/)
- [DFRobot_ID809_I2C library](https://github.com/DFRobot/DFRobot_ID809_I2C)
