# UNO R4 WiFi HTTPS diagnostic

Standalone transport check for the Azure migration. This sketch connects through
the existing Front Door to the public Keycloak discovery endpoint. It does not
change the E2E sketch, authenticate a user, fetch access policies or operate the
fingerprint reader, keypad or solenoid.

## Prepare and run

1. Disconnect the solenoid's **12 V supply** and power the UNO through USB.
   The sketch sets D9 LOW before configuring OUTPUT, but the supply must stay
   disconnected throughout this networking test, including resets/uploads.
2. Copy `arduino_secrets.example.h` to `arduino_secrets.h` and enter the Wi-Fi
   credentials locally. The latter is Git-ignored. No device shared key is needed.
3. Select **Arduino UNO R4 WiFi**. `WiFiS3` and `WiFiSSLClient` are included in the
   board package; no additional library is needed.
4. Compile and manually upload this sketch, then open Serial Monitor at
   **115200 baud**. E2E uses 9600 baud instead.

```powershell
arduino-cli compile --fqbn arduino:renesas_uno:unor4wifi hardware/arduino/https_test
arduino-cli upload --fqbn arduino:renesas_uno:unor4wifi --port COM5 hardware/arduino/https_test
```

Use the actual detected board port if it differs. An Arduino IDE installation
may bundle `arduino-cli.exe` without adding it to PATH.

## Expected checks

The diagnostic records firmware version and TLS connection timings, without
printing Wi-Fi credentials, SSID, device secrets or discovery response bodies.
It performs these checks in sequence:

1. Connect by DNS hostname to `idp.fingauth.conorwoods.co.uk:443` using the
   connectivity firmware's default trusted CA bundle. Request
   `/realms/fingauth/.well-known/openid-configuration` and require HTTP 200 and
   the exact issuer `https://idp.fingauth.conorwoods.co.uk/realms/fingauth`.
2. Close the connection and repeat the TLS handshake with **only ISRG Root X1**
   trusted. This is a well-formed public CA certificate deliberately unrelated
   to the DigiCert chain recorded in the validation run. Expect the connection to fail.
3. Restore the default bundle, close/reopen the connection, and require the
   same successful discovery check again. All three results are needed for
   `SUITE: PASS`; failure of the first positive control skips the negative test.

The negative test proves CA trust rejection only when the endpoint has an
unrelated issuer. If Front Door's issuing CA changes to ISRG in future, choose
a different unrelated test root. A failed handshake has no detailed certificate
error available through this sketch's API, so interpret it with the successful
before/after controls. This is not a separate proof of hostname-mismatch or
expired-certificate rejection.

Wi-Fi joins, TLS connections and HTTP processing have configured wait limits.
The HTTP reader bounds lines, headers and inspected body bytes, handles chunked
or length/connection-delimited responses, and stops once the issuer is checked.
Underlying synchronous WiFiS3/modem calls can extend those limits; this is not a
hard real-time watchdog. No validation bypass or leaf-certificate pinning is used.

## Repeat and recovery

Send a single letter through Serial Monitor:

| Command | Action |
| --- | --- |
| `R` | Reconnect if needed and repeat the complete suite |
| `D` | Close TLS and disconnect Wi-Fi |
| `S` | Report connectivity state and the D9 LOW command |

For a reconnection check, send `D`, confirm disconnected status with `S`, then
send `R` and require `SUITE: PASS`. For a cold-start check, physically unplug and
reconnect USB with the 12 V supply still disconnected, reopen the monitor and
check the startup suite. `R` alone is not a cold-start test.

If trust fails, inspect the connectivity firmware and time synchronisation
before changing CA settings. Updating connectivity firmware can overwrite the
uploaded sketch; re-upload this diagnostic afterwards.

To resume the bench setup, upload the unchanged `hardware/arduino/E2E_local` sketch
using its existing local secrets, and use Serial Monitor at 9600 baud. Keep the
12 V supply disconnected until the E2E firmware is restored and the normal
bench procedure is resumed.

## Evidence

See `validation.md` for recorded compile/hardware results and any pending checks.
