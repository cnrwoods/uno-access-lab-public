#pragma once
// Copy to arduino_secrets.h (Git-ignored), set Wi-Fi/host locally, and use the
// same private key as the portal. See src/managementportal/README.md#pin-setup.
// Initialize-PocDevice.ps1 creates the earlier remote-policy sketch's header.
#define SECRET_SSID "YOUR_WIFI_NAME"
#define SECRET_PASS "YOUR_WIFI_PASSWORD"
#define POLICY_HOST "192.168.1.100"
#define POLICY_PORT 5080
// Exactly 64 hex characters, matching the server's pre-shared 32-byte key.
#define SHARED_KEY_HEX ""
