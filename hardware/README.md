# Hardware

The current bench build uses an Arduino UNO R4 WiFi, DFRobot SEN0359/ID809
fingerprint reader, 3x4 membrane keypad, Adafruit 5648 MOSFET driver and a
separately powered 12 V lock-style solenoid. The UNO runs from USB.

The [completed wiring diagram](diagrams/hardware-completed.drawio.png) and its
[editable source](diagrams/hardware-completed.drawio) show the assembled layout.
The earlier `hardware.drawio` diagram is retained as a design reference.

## Connections

| Component | UNO connection |
| --- | --- |
| Keypad rows R1-R4 | D8, D7, D6, D5 |
| Keypad columns C1-C3 | D4, D3, D2 |
| Fingerprint adapter SDA / SCL | A4 / A5 |
| Fingerprint adapter VCC / GND | 5 V / GND |
| MOSFET driver IN | D9, active HIGH |
| MOSFET driver GND | Common ground with UNO and 12 V supply negative |

Identify keypad rows and columns from the manufacturer pinout or continuity
testing; ribbon position alone does not identify them. The sensor IRQ pin is
unused: examples that use IRQ on D6 conflict with this keypad wiring.

The 12 V supply positive feeds driver V+; the lock connects across the driver's
switched output. Check the terminal markings and supply polarity before
powering. Connect the lock negative to the switched output, not directly to
ground. Never connect 12 V to a UNO signal pin, 5 V pin or fingerprint adapter.
Leave UNO VIN and the barrel socket unused in this USB-powered arrangement.

## Firmware

| Sketch | Purpose | Serial baud |
| --- | --- | ---: |
| [E2E_remote](arduino/E2E_remote/README.md) | HTTPS policy, network time, fingerprint/PIN and solenoid | 9600 |
| [E2E_local](arduino/E2E_local/README.md) | LAN HTTP version with manually set test clock | 9600 |
| [https_test](arduino/https_test/README.md) | TLS trust and reconnect diagnostic | 115200 |
| `arduino/tick_display` | Steady tick on the built-in LED matrix | - |
| `arduino/access_display` | `allow` / `deny` serial commands show tick / cross for three seconds | 115200 |
| `arduino/keypad_membrane` | Keypad input and display exercises | See sketch |
| `arduino/SEN0359` | Sensor communication, enrolment and earlier matching exercises | See sketch |
| `arduino/solenoid/solenoid_test_sketch` | `U` serial command pulses D9 for two seconds | 115200 |

Open a sketch's matching `.ino` file, select **Arduino UNO R4 WiFi** and the
connected port, then verify before uploading. Uploading replaces the running
firmware. The LED matrix library comes with the UNO R4 board package.

The earlier `fingerprint_and_pin_withMatrix` sketch accepts the public demo PIN
`1234#` or any enrolled fingerprint (template IDs 1-80). `*` clears input; an
unfinished entry expires after 30 seconds. It displays and logs keypad digits,
has no attempt lockout and does not drive a solenoid. Use it only as an input
exercise. The `test_remote_policy` exercise uses the obsolete `/api/policy`
schema; it does not interoperate with the current policy API. Its Wi-Fi
configuration belongs in a local, ignored `arduino_secrets.h`, copied from the
example in that folder.

## Bench limits

Disconnect the solenoid's 12 V supply before wiring, uploading or running
unrelated sketches. Test inputs and networking with USB power first. Use brief,
supervised activations, allow the solenoid to cool, and check its rated duty
cycle before repeated operation. The driver includes flyback protection.

This build omits the recommended 10 kOhm pull-down on the MOSFET input and has
no independent pulse cutoff. Reset or a floating input can activate the driver;
synchronous sensor/modem calls can delay the software's nominal two-second
cutoff. Add a pull-down and independent, non-retriggerable cutoff before further
reliability work. This is a tabletop proof of concept, not production access
control. There is no lock-position sensor to confirm physical movement.

## Component references

- [UNO R4 WiFi documentation](https://docs.arduino.cc/hardware/uno-r4-wifi/)
- [DFRobot SEN0359 documentation and downloads](https://wiki.dfrobot.com/sen0359/)
- [Keypad pinouts](https://learn.adafruit.com/matrix-keypad/pinouts)
- [MOSFET driver connections and ratings](https://learn.adafruit.com/adafruit-mosfet-driver)
- [Adafruit 5065 lock-style solenoid](https://www.adafruit.com/product/5065)
