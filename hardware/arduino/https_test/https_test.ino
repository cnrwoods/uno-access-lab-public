#include <WiFiS3.h>
#include <WiFiSSLClient.h>
#include <ctype.h>
#include <stdlib.h>
#include <string.h>

#include "arduino_secrets.h"
#include "UnrelatedTestRoot.h"

// Standalone transport diagnostic. Keep the solenoid's 12 V supply off.
// No fingerprint, keypad, policy, PIN or actuator operations are performed.
constexpr uint8_t SOLENOID_PIN = 9;
constexpr char HOST[] = "idp.fingauth.conorwoods.co.uk";
constexpr char PATH[] = "/realms/fingauth/.well-known/openid-configuration";
constexpr char EXPECTED_ISSUER[] =
    "https://idp.fingauth.conorwoods.co.uk/realms/fingauth";
constexpr uint32_t WIFI_WAIT_MS = 30000;
constexpr uint32_t CONNECT_WAIT_MS = 10000;
constexpr uint32_t RESPONSE_WAIT_MS = 15000;
constexpr size_t MAX_BODY_BYTES = 16384;

WiFiSSLClient client;
bool suiteRunning = false;

bool within(uint32_t started, uint32_t duration) {
  return static_cast<uint32_t>(millis() - started) < duration;
}

int readByte(uint32_t started) {
  while (within(started, RESPONSE_WAIT_MS)) {
    if (client.available() > 0) return client.read();
    if (!client.connected()) return -1;
    delay(1);
  }
  return -1;
}

bool readLine(char* line, size_t capacity, uint32_t started) {
  size_t length = 0;
  for (;;) {
    const int value = readByte(started);
    if (value < 0) return false;
    if (value == '\n') {
      if (length > 0 && line[length - 1] == '\r') --length;
      line[length] = '\0';
      return true;
    }
    if (length + 1 >= capacity) return false;
    line[length++] = static_cast<char>(value);
  }
}

bool equalIgnoringCase(const char* left, const char* right) {
  while (*left && *right) {
    if (tolower(static_cast<unsigned char>(*left++)) !=
        tolower(static_cast<unsigned char>(*right++))) return false;
  }
  return *left == *right;
}

// Remove chunk framing before inspecting the JSON. No full response is stored.
struct BodyReader {
  uint32_t started;
  bool chunked;
  size_t remaining;
  size_t bytes = 0;
  bool needChunkEnd = false;

  int next() {
    if (bytes >= MAX_BODY_BYTES) return -1;
    if (chunked && remaining == 0) {
      if (needChunkEnd &&
          (readByte(started) != '\r' || readByte(started) != '\n')) return -1;
      char line[80];
      if (!readLine(line, sizeof(line), started)) return -1;
      char* end = nullptr;
      const unsigned long chunk = strtoul(line, &end, 16);
      if (end == line || (*end != '\0' && *end != ';') ||
          chunk == 0 || chunk > MAX_BODY_BYTES - bytes) return -1;
      remaining = static_cast<size_t>(chunk);
      needChunkEnd = true;
    }
    if (!chunked && remaining == 0) return -1;
    const int value = readByte(started);
    if (value < 0) return -1;
    --remaining;
    ++bytes;
    return value;
  }
};

bool verifyDiscovery(uint32_t started) {
  char line[1024];
  if (!readLine(line, sizeof(line), started)) {
    Serial.println("FAIL: HTTP status unavailable or timed out.");
    return false;
  }
  Serial.print("HTTP status: ");
  Serial.println(line);
  const char* status = strchr(line, ' ');
  if (strncmp(line, "HTTP/1.", 7) != 0 || status == nullptr ||
      strncmp(status, " 200 ", 5) != 0) return false;

  bool chunked = false;
  bool hasLength = false;
  size_t contentLength = MAX_BODY_BYTES;
  bool headersComplete = false;
  size_t headerBytes = 0;
  for (uint8_t count = 0; count < 40; ++count) {
    if (!readLine(line, sizeof(line), started)) return false;
    headerBytes += strlen(line) + 2;
    if (headerBytes > 8192) return false;
    if (line[0] == '\0') {
      headersComplete = true;
      break;
    }
    char* colon = strchr(line, ':');
    if (colon == nullptr) return false;
    *colon = '\0';
    char* value = colon + 1;
    while (*value == ' ' || *value == '\t') ++value;
    if (equalIgnoringCase(line, "Transfer-Encoding")) {
      if (!equalIgnoringCase(value, "chunked")) return false;
      chunked = true;
    } else if (equalIgnoringCase(line, "Content-Length")) {
      char* end = nullptr;
      const unsigned long length = strtoul(value, &end, 10);
      if (hasLength || end == value || *end != '\0' ||
          value[0] < '0' || value[0] > '9' || length > MAX_BODY_BYTES) return false;
      contentLength = static_cast<size_t>(length);
      hasLength = true;
    } else if (equalIgnoringCase(line, "Content-Encoding") &&
               !equalIgnoringCase(value, "identity")) return false;
  }
  if (!headersComplete || (chunked && hasLength)) return false;

  BodyReader body{started, chunked, chunked ? 0 : contentLength};
  constexpr char field[] = "\"issuer\"";
  size_t matched = 0;
  int value;
  while ((value = body.next()) >= 0) {
    if (value == field[matched]) {
      if (++matched == sizeof(field) - 1) break;
    } else {
      matched = value == field[0] ? 1 : 0;
    }
  }
  if (matched != sizeof(field) - 1) return false;
  do { value = body.next(); } while (value >= 0 && isspace(value));
  if (value != ':') return false;
  do { value = body.next(); } while (value >= 0 && isspace(value));
  if (value != '"') return false;
  char issuer[160];
  size_t length = 0;
  while ((value = body.next()) >= 0 && value != '"') {
    if (length + 1 >= sizeof(issuer) || value < 32 || value == '\\') return false;
    issuer[length++] = static_cast<char>(value);
  }
  if (value != '"') return false;
  issuer[length] = '\0';
  const bool matches = strcmp(issuer, EXPECTED_ISSUER) == 0;
  Serial.print("Expected discovery issuer: ");
  Serial.println(matches ? "PASS" : "FAIL");
  return matches;
}

bool trustedRequest(const char* label) {
  client.stop();
  client.setCACert(nullptr); // Use the connectivity firmware's CA bundle.
  client.setConnectionTimeout(CONNECT_WAIT_MS);
  Serial.println(label);
  const uint32_t connectedAt = millis();
  const bool connected = client.connect(HOST, 443) == 1;
  Serial.print("TLS connection: ");
  Serial.print(connected ? "PASS" : "FAIL");
  Serial.print(" (");
  Serial.print(static_cast<uint32_t>(millis() - connectedAt));
  Serial.println(" ms)");
  if (!connected) {
    client.stop();
    return false;
  }
  char request[320];
  const int size = snprintf(request, sizeof(request),
      "GET %s HTTP/1.1\r\nHost: %s\r\nAccept: application/json\r\n"
      "Accept-Encoding: identity\r\nConnection: close\r\n\r\n", PATH, HOST);
  const uint32_t responseAt = millis();
  const bool passed = size > 0 && static_cast<size_t>(size) < sizeof(request) &&
      client.write(reinterpret_cast<const uint8_t*>(request), size) ==
          static_cast<size_t>(size) && verifyDiscovery(responseAt);
  client.stop();
  Serial.println(passed ? "Trusted discovery request: PASS" :
                          "Trusted discovery request: FAIL");
  return passed;
}

bool unrelatedCaRejected() {
  client.stop();
  client.setCACert(UNRELATED_TEST_ROOT);
  Serial.println("Test 2: same hostname with unrelated ISRG Root X1 only.");
  const uint32_t started = millis();
  const bool connected = client.connect(HOST, 443) == 1;
  client.stop();
  client.setCACert(nullptr);
  Serial.print("Unrelated CA rejected: ");
  Serial.print(connected ? "FAIL (unexpected TLS connection)" : "PASS");
  Serial.print(" (");
  Serial.print(static_cast<uint32_t>(millis() - started));
  Serial.println(" ms)");
  return !connected;
}

bool connectWifi() {
  if (WiFi.status() == WL_CONNECTED) return true;
  Serial.println("Connecting to Wi-Fi (credentials are not logged).");
  const uint32_t started = millis();
  WiFi.setTimeout(1000);
  while (within(started, WIFI_WAIT_MS)) {
    WiFi.begin(SECRET_SSID, SECRET_PASS);
    for (uint8_t wait = 0; wait < 20 && within(started, WIFI_WAIT_MS); ++wait) {
      if (WiFi.status() == WL_CONNECTED) {
        Serial.println("Wi-Fi connection: PASS");
        return true;
      }
      delay(250);
    }
  }
  Serial.println("Wi-Fi connection: FAIL (bounded wait expired).");
  return false;
}

void runSuite() {
  if (suiteRunning) return;
  suiteRunning = true;
  Serial.println("BEGIN HTTPS SUITE");
  Serial.print("Connectivity firmware: ");
  Serial.println(WiFi.firmwareVersion());
  if (!connectWifi()) {
    Serial.println("SUITE: FAIL (Wi-Fi unavailable)");
  } else {
    // Connectivity firmware synchronises its TLS clock independently of E2E RTC.
    delay(2000);
    const bool first = trustedRequest("Test 1: default CA bundle.");
    bool rejected = false;
    bool restored = false;
    if (first) {
      delay(250);
      rejected = unrelatedCaRejected();
      delay(250);
      restored = trustedRequest("Test 3: default CA bundle restored.");
    } else {
      Serial.println("CA rejection test: SKIPPED (no successful control connection).");
    }
    Serial.println(first && rejected && restored ? "SUITE: PASS" : "SUITE: FAIL");
  }
  client.stop();
  suiteRunning = false;
  Serial.println("Send R to repeat, D to disconnect Wi-Fi, or S for status.");
}

void setup() {
  digitalWrite(SOLENOID_PIN, LOW);
  pinMode(SOLENOID_PIN, OUTPUT);
  Serial.begin(115200);
  const uint32_t started = millis();
  while (!Serial && within(started, 5000)) delay(10);
  Serial.println("UNO R4 WiFi standalone HTTPS diagnostic v1");
  if (WiFi.status() == WL_NO_MODULE) {
    Serial.println("FAIL: connectivity module unavailable.");
    return;
  }
  Serial.print("Connectivity firmware: ");
  Serial.println(WiFi.firmwareVersion());
  Serial.print("Board package expects at least: ");
  Serial.println(WIFI_FIRMWARE_LATEST_VERSION);
  runSuite();
}

void loop() {
  // The diagnostic never drives D9 HIGH, including during error handling.
  digitalWrite(SOLENOID_PIN, LOW);
  if (Serial.available()) {
    const char command = Serial.read();
    if (command == 'R' || command == 'r') runSuite();
    else if (command == 'D' || command == 'd') {
      client.stop();
      WiFi.disconnect();
      Serial.println("Wi-Fi disconnected. Send R to reconnect and repeat.");
    } else if (command == 'S' || command == 's') {
      Serial.print("Wi-Fi connected: ");
      Serial.println(WiFi.status() == WL_CONNECTED ? "yes" : "no");
      Serial.println("D9 commanded LOW; 12 V supply must remain disconnected.");
    }
  }
  delay(10);
}
