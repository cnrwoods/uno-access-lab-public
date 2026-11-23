# ADR-0001: Use Arduino UNO R4 WiFi

**Accepted:** 13 September 2026. **Owner:** Conor Woods.

## Decision

Use the Arduino UNO R4 WiFi as the bench controller, programmed through the UNO
R4 board package. Its onboard matrix provides grant, deny and fault feedback;
WiFiS3 supplies outbound network connectivity. The RA4M1 runs the application
and the ESP32-S3 connectivity firmware handles networking.

The integrated indicators and accessible Arduino examples suit a first
electronics project. An ESP32-S3 development board was considered, but would
require a different application and indicator arrangement. UNO R3 and R4 Minima
do not provide the selected combination of Wi-Fi and matrix.

## Consequences

- Keep policy payloads bounded: application SRAM is 32 KB and flash is 256 KB.
  The connectivity processor's memory is not application memory.
- Devices initiate outbound connections. No inbound home-router forwarding or
  internet-facing device server is required.
- Validate TLS on the actual board and connectivity firmware. Device identity
  and protected key storage remain separate work.
- USB flashing does not establish device-enforced secure boot or secure OTA.

The original reader/UART voltage-conversion proposal was superseded by
[ADR-0002](0002-use-solder-free-fingerprint-kit.md). Current wiring and sketches
are in the [hardware guide](../../../hardware/README.md).

## References

- [Arduino UNO R4 WiFi documentation](https://docs.arduino.cc/hardware/uno-r4-wifi/)
- [Board datasheet](https://docs.arduino.cc/resources/datasheets/ABX00087-datasheet.pdf)
