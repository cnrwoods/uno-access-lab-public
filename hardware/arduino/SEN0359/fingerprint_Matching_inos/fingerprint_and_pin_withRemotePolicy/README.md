# Remote policy fingerprint/PIN bench sketch

This separate UNO R4 WiFi sketch fetches the portal's active policy from `GET /api/policy/active` and evaluates it locally. It preserves the existing fingerprint and keypad wiring: keypad rows `{8,7,6,5}`, columns `{4,3,2}`, and the ID809's existing I2C connection. No enrolment, template deletion or actuator output is included. The earlier tested sketches are unchanged.

## Configure and compile

From the repository root, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Initialize-PocDevice.ps1` to create matching local registration files for the portal and sketch. Edit the generated, Git-ignored `arduino_secrets.h`: fill in `SECRET_SSID`, `SECRET_PASS` and the computer's LAN IPv4 address in `POLICY_HOST`. `POLICY_PORT` defaults to `5080`. Keep `SHARED_KEY_HEX` matched to the portal key; never paste either key into logs or source control. Restart the portal after provisioning.

For a LAN bench session, run the combined portal host from the repository root:

```powershell
dotnet run --project src/managementportal
```

The computer and UNO must share a network that permits this connection, and the computer firewall must allow the chosen port. `localhost` on the UNO does not refer to the computer. Keep this anonymous HTTP demonstration on a trusted, isolated lab network.

Select **Arduino UNO R4 WiFi**. Required libraries:

- DFRobot_ID809_I2C and Adafruit Keypad, as used by the existing sketch.
- ArduinoJson **7**, ArduinoHttpClient, and **Crypto by Rhys Weatherley**.
- WiFiS3, RTC and Arduino_LED_Matrix supplied by the UNO R4 board package.

Open `fingerprint_and_pin_withRemotePolicy.ino`. Compile before uploading. The registration header must exist; there is deliberately no hard-coded fallback key or PIN. Serial Monitor uses **9600 baud**. No board is flashed by repository setup or compilation.

The sketch compiled successfully with UNO R4 core 1.6.0, ArduinoJson 7.4.3, ArduinoHttpClient 0.6.2 and Crypto 0.4.0. Physical Wi-Fi, keypad and fingerprint behaviour still requires the board checks below.

## Behaviour

The portal selects one active policy. Supported schema 2 modes are `fingerprintOnly`, `pinOnly`, `fingerprintOrPin` and `denyAll`. Fingerprint-or-PIN permits either method; it is not two-factor authentication. Any successfully matched sensor template ID from 1 through 80 is accepted when fingerprint access is permitted.

The daily window is inclusive at `startMinute` and exclusive at `endMinute`, expressed as minutes since midnight. It is combined with `any`, `even` or `odd` minute parity. The window must fit within one day. A restricted time/parity policy denies access until you send `TIME HH:MM` followed by a newline through Serial Monitor. For example, `TIME 13:32` sets the test RTC to 13:32. `TIME CLEAR` invalidates it. There is no timezone conversion or daylight-saving handling: use the same chosen wall-clock convention in the portal and on the UNO. This manually trusted test clock is invalid after every reboot. A full-day, `any` policy does not need a valid clock.

Enter a 4–8 digit PIN and press `#`; `*` clears the entire entry. Leading zeroes are preserved. Extra digits invalidate the whole attempt. The matrix shows the same neutral square for each digit, then a tick/cross for the result; neither PIN digits nor the key are printed or displayed. Partial entry expires after 30 seconds and is cleared when the policy changes or becomes unavailable. Five rejected PIN submissions lock PIN entry for 30 seconds. Polling, policy changes and fingerprint success do not clear that lockout. Counters are in RAM and reset on power loss; fingerprint access remains available when its policy permits it.

Policy polling runs every 10 seconds. A 204 response means no active policy. Any fetch failure, unsupported/malformed policy, disabled response or mismatched PIN key ID invalidates the cached policy and denies access. A successful response expires after 30 seconds without another success. Responses are limited to 2048 body bytes and 4096 total HTTP bytes, with a 4-second receive budget, a 250 ms HTTP inactivity timeout and a 1.5-second TCP connection timeout. Wi-Fi association has a 1.5-second timeout. The WiFiS3 modem and fingerprint library calls are synchronous and can briefly pause keypad scanning; their internal driver calls can add time beyond these application budgets. Short key taps during network or fingerprint operations may be missed. All decision paths recheck policy expiry, and fingerprint decisions also recheck the clock after capture/search.

## PIN interoperability and limits

`PinVerifier.h` computes HMAC-SHA256 using the raw 32-byte shared key. The exact message is UTF-8 text:

```text
uno-access-lab/pin/v1\n<exact policyId>\n<32 lowercase salt hex characters>\n<PIN>
```

Here `\n` means one newline byte. There is no trailing newline or NUL. Salt is hexadecimal **text**, not decoded bytes. `keyId` is the first eight bytes of SHA-256 of the raw key, encoded as 16 lowercase hex characters. The 32-byte HMAC result is compared without an early exit. The key is never downloaded from the API.

Boot runs this public test vector before accepting policies:

| Value | Test input/output |
|---|---|
| Key | bytes `00` through `1f` |
| Policy ID | `POL-001` |
| Salt | `000102030405060708090a0b0c0d0e0f` |
| PIN | `0007` |
| Key ID | `630dcd2966c43366` |
| HMAC | `ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f` |

The [Crypto SHA256 API](https://rweather.github.io/arduinolibs/classSHA256.html) provides `resetHMAC`, `update` and `finalizeHMAC`; [Crypto's utility implementation](https://github.com/rweather/arduinolibs/blob/master/libraries/Crypto/Crypto.cpp) provides `secure_compare` and memory clearing. ArduinoJson's [type checks](https://arduinojson.org/v7/api/jsonvariant/is/) are used before conversion. The HTTP bounds supplement [ArduinoHttpClient's response handling](https://github.com/arduino-libraries/ArduinoHttpClient/blob/master/src/HttpClient.cpp).

The shared key prevents offline PIN guessing from a downloaded verifier alone; it does **not** authenticate the other policy rules or provide encrypted HTTP transport, replay protection, device authentication or tamper-resistant key storage. The current portal authenticates administrators through Keycloak; the device API remains anonymous. A device/key compromise exposes short PINs to offline guessing. This remains an LED-only bench demonstration.

## Board checks before relying on the demonstration

1. Confirm the public self-test passes at boot; a wrong local key must reject PIN policies.
2. Activate each mode in the portal. Confirm the allowed authentication methods and `denyAll` behaviour.
3. Try a correct PIN with leading zeroes, a wrong PIN, an empty submission and more than eight digits. Confirm no digits appear in Serial Monitor or on the matrix.
4. Submit five wrong PINs. Confirm polling and switching policies do not clear the 30-second lockout.
5. Try restricted policies before setting the clock, then test window boundaries and both minute parities using `TIME HH:MM`.
6. Pause all policies, stop the server and disconnect Wi-Fi. Confirm each condition removes access after the next poll, and stale policies never grant after 30 seconds.
7. Begin a PIN, change the active policy, wait for the refresh and submit. Confirm the old partial entry was discarded.
8. Confirm valid/invalid fingerprints, lift-to-retry behaviour and keypad responsiveness on the actual board. Compilation alone does not verify these hardware interactions.
