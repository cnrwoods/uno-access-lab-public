
/*
 * Fingerprint Entry
 * Local .NET API connectivity test
 *
 * Arduino UNO R4 WiFi
 */

#include <WiFiS3.h>
#include <ArduinoJson.h>

// ===========================================
// NETWORK CONFIGURATION
// ===========================================

#include "arduino_secrets.h"

// Legacy connectivity exercise: requires the old /api/policy schema.
// The current application uses /api/policy/active; use E2E_remote for that.
const char* API_PATH = "/api/policy";

// ===========================================
// POLICY STORAGE
// ===========================================

// This structure holds the downloaded policy.
struct AccessPolicy {
  bool enabled;
  uint8_t allowedMinuteParity;
  int version;
  bool valid;
};

// Start with no valid policy (fail closed).
AccessPolicy policy = {false, 0, 0, false};

// Network client for making HTTP requests.
WiFiClient client;

// Refresh policy every 60 seconds.
const unsigned long POLICY_INTERVAL = 60000;
unsigned long lastPolicyFetch = 0;


// ===========================================
// CONNECT TO WI-FI
// ===========================================

void connectWiFi() {

  Serial.print("Connecting to Wi-Fi");

  WiFi.begin(SECRET_SSID, SECRET_PASS);

  while (WiFi.status() != WL_CONNECTED) {
    Serial.print(".");
    delay(1000);
  }

  Serial.println();
  Serial.println("Wi-Fi connected!");

  Serial.print("Arduino IP: ");
  Serial.println(WiFi.localIP());
}


// ===========================================
// FETCH POLICY FROM .NET API
// ===========================================

void fetchPolicy() {

  Serial.println();
  Serial.println("Fetching policy from .NET API...");

  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("Wi-Fi disconnected");
    policy.valid = false;
    return;
  }

  // Connect to the locally hosted HTTP endpoint.
  if (!client.connect(POLICY_HOST, POLICY_PORT)) {
    Serial.println("ERROR: API connection failed");
    policy.valid = false;
    return;
  }

  client.setTimeout(3000);

  // Send HTTP GET request.
  client.print("GET ");
  client.print(API_PATH);
  client.println(" HTTP/1.1");

  client.print("Host: ");
  client.println(POLICY_HOST);

  client.println("Connection: close");
  client.println();

  // Wait up to 5 seconds for a response.
  unsigned long start = millis();

  while (!client.available() &&
         client.connected() &&
         millis() - start < 5000) {
    delay(10);
  }

  if (!client.available()) {
    Serial.println("ERROR: API response timeout");
    client.stop();
    policy.valid = false;
    return;
  }

  // Read HTTP response status.
  String statusLine = client.readStringUntil('\n');
  statusLine.trim();

  Serial.println(statusLine);

  if (!statusLine.startsWith("HTTP/1.1 200") &&
      !statusLine.startsWith("HTTP/1.0 200")) {
    Serial.println("ERROR: HTTP request unsuccessful");
    client.stop();
    policy.valid = false;
    return;
  }

  // Skip HTTP headers until the blank line.
  while (client.connected() || client.available()) {
    String line = client.readStringUntil('\n');
    line.trim();
    if (line.length() == 0) break;
  }

  // Read the JSON response body.
  // Suitable for our small local API response.
  String response = client.readString();

  client.stop();

  Serial.println("Received JSON:");
  Serial.println(response);

  // Parse JSON.
  JsonDocument doc;
  DeserializationError error =
    deserializeJson(doc, response);

  if (error) {
    Serial.println("ERROR: Invalid JSON");
    policy.valid = false;
    return;
  }

  // Validate expected policy fields.
  if (!doc["enabled"].is<bool>() ||
      !doc["version"].is<int>() ||
      !doc["allowedMinuteParity"].is<const char*>()) {
    Serial.println("ERROR: Missing or invalid policy fields");
    policy.valid = false;
    return;
  }

  String parity =
    doc["allowedMinuteParity"].as<String>();

  if (parity != "even" && parity != "odd") {
    Serial.println("ERROR: Unknown parity value");
    policy.valid = false;
    return;
  }

  // Update the locally cached policy.
  policy.enabled = doc["enabled"].as<bool>();
  policy.version = doc["version"].as<int>();
  policy.allowedMinuteParity =
    (parity == "even") ? 0 : 1;

  policy.valid = true;

  Serial.println("Policy updated successfully!");

  Serial.print("Policy version: ");
  Serial.println(policy.version);

  Serial.print("Enabled: ");
  Serial.println(policy.enabled ? "YES" : "NO");

  Serial.print("Allowed minutes: ");
  Serial.println(parity);
}


// ===========================================
// SETUP
// ===========================================

void setup() {

  Serial.begin(115200);
  delay(2000);

  Serial.println("Fingerprint Entry - API Test");

  connectWiFi();

  // Download policy immediately on startup.
  fetchPolicy();

  lastPolicyFetch = millis();
}


// ===========================================
// MAIN LOOP
// ===========================================

void loop() {

  // Refresh periodically without using
  // a 60-second blocking delay.
  if (millis() - lastPolicyFetch >= POLICY_INTERVAL) {

    lastPolicyFetch = millis();

    if (WiFi.status() != WL_CONNECTED) {
      connectWiFi();
    }

    fetchPolicy();
  }
}
