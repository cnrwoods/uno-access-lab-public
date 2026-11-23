#pragma once
// Run scripts/Initialize-PocDevice.ps1 to create arduino_secrets.h with a
// random key matching the app. Edit Wi-Fi and host settings only in that file.
#define SECRET_SSID "YOUR_WIFI_NAME"
#define SECRET_PASS "YOUR_WIFI_PASSWORD"
#define POLICY_HOST "192.168.1.100"
#define POLICY_PORT 5080
// Exactly 64 hex characters, matching the server's pre-shared 32-byte key.
#define SHARED_KEY_HEX ""
