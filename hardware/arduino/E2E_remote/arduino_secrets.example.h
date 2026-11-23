#pragma once
// Copy to arduino_secrets.h (Git-ignored). Preserve the EXISTING shared key
// from E2E_local and the portal; do not generate a replacement for migration.
#define SECRET_SSID "YOUR_WIFI_NAME"
#define SECRET_PASS "YOUR_WIFI_PASSWORD"
#define POLICY_HOST "fingauth.conorwoods.co.uk"
#define POLICY_PORT 443
#define TIME_API_HOST "timeapi.io"
#define TIME_API_PATH "/api/time/current/zone?timeZone=UTC"
// Exactly 64 hex characters, matching the server's pre-shared 32-byte key.
#define SHARED_KEY_HEX ""
