
//
// UNO Access Lab
// Arduino UNO R4 WiFi + ID809 fingerprint + keypad
//
// Remote policy selected over LAN HTTP.
// Fingerprint/PIN decisions remain local.
// Validated local PERMIT additionally pulses the D9 solenoid output.
//
// HTTP revision:
// - Direct WiFiClient
// - Single-write HTTP request
// - Bounded HTTP response reader
// - Fail-closed policy validation
//
// Original DFRobot example copyright
// (c) 2010 DFRobot Co.Ltd, MIT.
//

#include <DFRobot_ID809_I2C.h>
#include <Arduino_LED_Matrix.h>
#include <Adafruit_Keypad.h>
#include <RTC.h>
#include <WiFiS3.h>
#include <ArduinoJson.h>
#include <stdio.h>
#include <string.h>

#include "PinVerifier.h"
#include "arduino_secrets.h"
#include "SolenoidController.h"

// ============================================================
// CONFIGURATION
// ============================================================

const unsigned long POLICY_POLL_MS = 10000;
const unsigned long POLICY_MAX_AGE_MS = 30000;

const unsigned long HTTP_DEADLINE_MS = 10000;
const unsigned long WIFI_SETTLE_MS = 1000;
const unsigned long WIFI_STARTUP_WAIT_MS = 15000;

const size_t MAX_HEADER_BYTES = 2048;
const size_t MAX_WIRE_BYTES = 4096;
const size_t MAX_HEADER_LINE_BYTES = 192;
const size_t MAX_BODY_BYTES = 2048;

const unsigned long PIN_TIMEOUT_MS = 30000;
const unsigned long PIN_LOCK_MS = 30000;
const unsigned long DISPLAY_DURATION_MS = 3000;

// ============================================================
// POLICY MODEL
// ============================================================

enum AccessMode : uint8_t {
  FINGERPRINT_ONLY,
  PIN_ONLY,
  FINGERPRINT_OR_PIN,
  DENY_ALL
};

struct RemotePolicy {
  char id[65];
  int32_t version;
  AccessMode mode;
  int8_t parity;
  uint16_t startMinute;
  uint16_t endMinute;
  char salt[33];
  uint8_t pinHash[32];
};

RemotePolicy policy = {};

bool policyReady = false;
unsigned long policyReceived = 0;
unsigned long lastPolicyPoll = 0;
bool firstPolicyPoll = true;

uint8_t sharedKey[32] = {};
bool cryptoReady = false;

// ============================================================
// NETWORK STATE
// ============================================================

IPAddress policyServer;
WiFiClient policyClient;

unsigned long wifiConnectedAt = 0;
bool wifiWasConnected = false;

// ============================================================
// CLOCK / SERIAL STATE
// ============================================================

bool clockValid = false;
char serialInput[11] = {};
uint8_t serialLength = 0;
bool serialOverflow = false;

// ============================================================
// HARDWARE
// ============================================================

DFRobot_ID809_I2C fingerprint;
ArduinoLEDMatrix matrix;

const byte ROWS = 4;
const byte COLS = 3;

char keys[ROWS][COLS] = {
  {'1', '2', '3'},
  {'4', '5', '6'},
  {'7', '8', '9'},
  {'*', '0', '#'}
};

// Existing tested wiring.
byte rowPins[ROWS] = {8, 7, 6, 5};
byte colPins[COLS] = {4, 3, 2};

Adafruit_Keypad keypad(
  makeKeymap(keys),
  rowPins,
  colPins,
  ROWS,
  COLS
);

// ============================================================
// PIN STATE
// ============================================================

char enteredPin[9] = {};
uint8_t pinLength = 0;
bool pinOverflow = false;

unsigned long lastPinActivity = 0;
uint8_t failedPinAttempts = 0;

bool pinLockActive = false;
unsigned long pinLockStarted = 0;

// ============================================================
// FINGERPRINT / DISPLAY STATE
// ============================================================

bool sensorReady = false;
bool waitingForFingerRelease = false;

unsigned long lastSensorPoll = 0;

bool showingResult = false;
unsigned long resultStarted = 0;

uint8_t tick[8][12] = {
  {0,0,0,0,0,0,0,0,0,0,0,0},
  {0,0,0,0,0,0,0,0,0,1,1,0},
  {0,0,0,0,0,0,0,0,1,1,0,0},
  {0,0,0,0,0,0,0,1,1,0,0,0},
  {0,1,1,0,0,0,1,1,0,0,0,0},
  {0,0,1,1,0,1,1,0,0,0,0,0},
  {0,0,0,1,1,1,0,0,0,0,0,0},
  {0,0,0,0,1,0,0,0,0,0,0,0}
};

uint8_t cross[8][12] = {
  {0,1,1,0,0,0,0,0,0,1,1,0},
  {0,0,1,1,0,0,0,0,1,1,0,0},
  {0,0,0,1,1,0,0,1,1,0,0,0},
  {0,0,0,0,1,1,1,1,0,0,0,0},
  {0,0,0,0,1,1,1,1,0,0,0,0},
  {0,0,0,1,1,0,0,1,1,0,0,0},
  {0,0,1,1,0,0,0,0,1,1,0,0},
  {0,1,1,0,0,0,0,0,0,1,1,0}
};

uint8_t neutral[8][12] = {
  {0,0,0,0,0,0,0,0,0,0,0,0},
  {0,0,0,0,0,0,0,0,0,0,0,0},
  {0,0,0,0,1,1,1,1,0,0,0,0},
  {0,0,0,0,1,0,0,1,0,0,0,0},
  {0,0,0,0,1,0,0,1,0,0,0,0},
  {0,0,0,0,1,1,1,1,0,0,0,0},
  {0,0,0,0,0,0,0,0,0,0,0,0},
  {0,0,0,0,0,0,0,0,0,0,0,0}
};

// ============================================================
// ACCESS MODE HELPERS
// ============================================================

bool usesPin(AccessMode mode) {
  return mode == PIN_ONLY ||
         mode == FINGERPRINT_OR_PIN;
}

bool usesFingerprint(AccessMode mode) {
  return mode == FINGERPRINT_ONLY ||
         mode == FINGERPRINT_OR_PIN;
}

bool asciiLetterOrDigit(char c) {
  return (c >= 'A' && c <= 'Z') ||
         (c >= 'a' && c <= 'z') ||
         (c >= '0' && c <= '9');
}

bool jsonSpace(char c) {
  return c == ' ' ||
         c == '\t' ||
         c == '\r' ||
         c == '\n';
}

// ============================================================
// JSON ENVELOPE VALIDATION
// ============================================================

// Strict policy schema.
// Rejects duplicate/unknown keys, escaped strings,
// arrays and trailing data.

bool checkJsonEnvelope(const char* body, size_t length) {
  const char* fields[] = {
    "policyId",
    "version",
    "enabled",
    "allowedMinuteParity",
    "schemaVersion",
    "accessMode",
    "startMinute",
    "endMinute",
    "pin",
    "algorithm",
    "salt",
    "hash",
    "keyId"
  };

  uint16_t seen = 0;
  int depth = 0;

  bool started = false;
  bool finished = false;

  for (size_t pos = 0; pos < length; ++pos) {
    char c = body[pos];

    if (finished) {
      if (!jsonSpace(c)) return false;
      continue;
    }

    if (!started && jsonSpace(c)) continue;
    if (!started && c != '{') return false;

    if (c == '{') {
      started = true;
      if (++depth > 2) return false;
    }
    else if (c == '}') {
      if (--depth < 0) return false;
      if (depth == 0) finished = true;
    }
    else if (
      c == '[' ||
      c == ']' ||
      c == '\\' ||
      c == '\0'
    ) {
      return false;
    }
    else if (c == '"') {
      size_t start = ++pos;

      while (pos < length && body[pos] != '"') {
        if ((uint8_t)body[pos] < 32 ||
            (uint8_t)body[pos] > 126 ||
            body[pos] == '\\') {
          return false;
        }
        ++pos;
      }

      if (pos == length) return false;

      size_t after = pos + 1;

      while (after < length && jsonSpace(body[after])) {
        ++after;
      }

      if (after < length && body[after] == ':') {
        int found = -1;

        for (uint8_t index = 0; index < 13; ++index) {
          if (strlen(fields[index]) == pos - start &&
              strncmp(
                body + start,
                fields[index],
                pos - start
              ) == 0) {
            found = index;
          }
        }

        if (found < 0 ||
            (seen & (1U << found)) ||
            depth != (found < 9 ? 1 : 2)) {
          return false;
        }

        seen |= 1U << found;
      }
    }
  }

  return started &&
         finished &&
         depth == 0 &&
         (seen & 0x1ff) == 0x1ff;
}

// ============================================================
// POLICY PARSING
// ============================================================

bool parsePolicy(
  const char* body,
  size_t length,
  RemotePolicy& candidate
) {
  if (!checkJsonEnvelope(body, length)) return false;

  JsonDocument document;

  if (deserializeJson(
        document,
        body,
        length,
        DeserializationOption::NestingLimit(2)
      )) {
    return false;
  }

  if (!document.is<JsonObject>()) return false;

  JsonObjectConst root = document.as<JsonObjectConst>();

  if (root.size() != 9 ||
      !root["schemaVersion"].is<int>() ||
      root["schemaVersion"].as<int>() != 2 ||
      !root["version"].is<int32_t>() ||
      root["version"].as<int32_t>() < 1 ||
      !root["enabled"].is<bool>() ||
      !root["enabled"].as<bool>() ||
      !root["policyId"].is<const char*>() ||
      !root["accessMode"].is<const char*>() ||
      !root["allowedMinuteParity"].is<const char*>() ||
      !root["startMinute"].is<int>() ||
      !root["endMinute"].is<int>()) {
    return false;
  }

  const char* id = root["policyId"].as<const char*>();
  size_t idLength = strlen(id);

  if (idLength == 0 ||
      idLength > 64 ||
      !asciiLetterOrDigit(id[0])) {
    return false;
  }

  for (size_t index = 0; index < idLength; ++index) {
    if (!asciiLetterOrDigit(id[index]) &&
        id[index] != '-' &&
        id[index] != '_') {
      return false;
    }
  }

  strcpy(candidate.id, id);
  candidate.version = root["version"].as<int32_t>();

  const char* mode = root["accessMode"].as<const char*>();

  if (strcmp(mode, "fingerprintOnly") == 0) {
    candidate.mode = FINGERPRINT_ONLY;
  }
  else if (strcmp(mode, "pinOnly") == 0) {
    candidate.mode = PIN_ONLY;
  }
  else if (strcmp(mode, "fingerprintOrPin") == 0) {
    candidate.mode = FINGERPRINT_OR_PIN;
  }
  else if (strcmp(mode, "denyAll") == 0) {
    candidate.mode = DENY_ALL;
  }
  else {
    return false;
  }

  const char* parity =
    root["allowedMinuteParity"].as<const char*>();

  if (strcmp(parity, "any") == 0) {
    candidate.parity = -1;
  }
  else if (strcmp(parity, "even") == 0) {
    candidate.parity = 0;
  }
  else if (strcmp(parity, "odd") == 0) {
    candidate.parity = 1;
  }
  else {
    return false;
  }

  int start = root["startMinute"].as<int>();
  int end = root["endMinute"].as<int>();

  if (start < 0 || start > 1439 ||
      end <= start || end > 1440) {
    return false;
  }

  candidate.startMinute = start;
  candidate.endMinute = end;

  if (!usesPin(candidate.mode)) {
    return root["pin"].isNull();
  }

  if (!root["pin"].is<JsonObjectConst>()) {
    return false;
  }

  JsonObjectConst pin = root["pin"].as<JsonObjectConst>();

  if (pin.size() != 4 ||
      !pin["algorithm"].is<const char*>() ||
      !pin["salt"].is<const char*>() ||
      !pin["hash"].is<const char*>() ||
      !pin["keyId"].is<const char*>() ||
      strcmp(
        pin["algorithm"].as<const char*>(),
        "HMAC-SHA256"
      ) != 0) {
    return false;
  }

  uint8_t saltBytes[16] = {};

  if (!PinVerifier::decodeHex(
        pin["salt"].as<const char*>(),
        saltBytes,
        sizeof(saltBytes)) ||
      !PinVerifier::decodeHex(
        pin["hash"].as<const char*>(),
        candidate.pinHash,
        sizeof(candidate.pinHash)) ||
      !PinVerifier::matchesKeyId(
        sharedKey,
        pin["keyId"].as<const char*>())) {
    return false;
  }

  strcpy(candidate.salt, pin["salt"].as<const char*>());
  return true;
}

bool samePolicy(
  const RemotePolicy& left,
  const RemotePolicy& right
) {
  return strcmp(left.id, right.id) == 0 &&
         left.version == right.version &&
         left.mode == right.mode &&
         left.parity == right.parity &&
         left.startMinute == right.startMinute &&
         left.endMinute == right.endMinute &&
         strcmp(left.salt, right.salt) == 0 &&
         secure_compare(
           left.pinHash,
           right.pinHash,
           sizeof(left.pinHash)
         );
}

// ============================================================
// PIN / DISPLAY HELPERS
// ============================================================

void resetPin() {
  clean(enteredPin);
  pinLength = 0;
  pinOverflow = false;
}

void blueRing() {
  if (sensorReady) {
    fingerprint.ctrlLED(
      fingerprint.eBreathing,
      fingerprint.eLEDBlue,
      0
    );
  }
}

void clearResult() {
  matrix.clear();
  showingResult = false;
  blueRing();
}

void invalidatePolicy(const char* reason) {
  // De-energise before display/sensor operations, which may block.
  lockSolenoid();
  policyReady = false;
  clean(policy);
  resetPin();
  clearResult();
  Serial.println(reason);
}

// ============================================================
// SCHEDULE CHECK
// ============================================================

bool scheduleAllows() {
  if (!cryptoReady ||
      !policyReady ||
      millis() - policyReceived >= POLICY_MAX_AGE_MS ||
      policy.mode == DENY_ALL ||
      WiFi.status() != WL_CONNECTED) {
    return false;
  }

  bool needsClock =
    policy.startMinute != 0 ||
    policy.endMinute != 1440 ||
    policy.parity != -1;

  if (!needsClock) return true;

  RTCTime now;

  if (!clockValid ||
      !RTC.getTime(now) ||
      now.getHour() > 23 ||
      now.getMinutes() > 59) {
    return false;
  }

  int minute = now.getHour() * 60 + now.getMinutes();

  return minute >= policy.startMinute &&
         minute < policy.endMinute &&
         (policy.parity == -1 ||
          now.getMinutes() % 2 == policy.parity);
}

// ============================================================
// SOLENOID SAFETY SERVICE
// ============================================================

void serviceSolenoidSafety() {
  // Check expiry before any potentially synchronous Wi-Fi/RTC call.
  serviceSolenoid();

  if (!solenoidIsActive()) return;

  if (!scheduleAllows()) {
    lockSolenoid();
  }

  serviceSolenoid();
}

// ============================================================
// PIN LOCKOUT
// ============================================================

bool pinLocked() {
  if (pinLockActive &&
      millis() - pinLockStarted >= PIN_LOCK_MS) {
    pinLockActive = false;
    failedPinAttempts = 0;
    Serial.println("PIN lockout ended.");
  }

  return pinLockActive;
}

// ============================================================
// ACCESS RESULT
// ============================================================

void renderAccessFeedback(bool allowed) {
  if (allowed) {
    matrix.renderBitmap(tick, 8, 12);
  }
  else {
    matrix.renderBitmap(cross, 8, 12);
  }

  if (sensorReady) {
    if (allowed) {
      fingerprint.ctrlLED(
        fingerprint.eKeepsOn,
        fingerprint.eLEDGreen,
        0
      );
    }
    else {
      fingerprint.ctrlLED(
        fingerprint.eKeepsOn,
        fingerprint.eLEDRed,
        0
      );
    }
  }

}

void showResult(bool allowed, const char* method) {
  resetPin();

  // Both callers supply their complete local authentication decision.
  // Recheck policy, time and connectivity before displaying success.
  allowed = allowed && scheduleAllows();
  if (!allowed) lockSolenoid();

  renderAccessFeedback(allowed);

  // Sensor LED commands are synchronous. Do not energise until they
  // finish, and reject an approval that expired/lost Wi-Fi meanwhile.
  if (allowed && !scheduleAllows()) {
    allowed = false;
    lockSolenoid();
    renderAccessFeedback(false);
  }

  if (allowed) {
    unlockSolenoid();
  }

  showingResult = true;
  resultStarted = millis();

  Serial.print(method);
  Serial.println(allowed ? ": ALLOW" : ": DENY");
}

// ============================================================
// KEYPAD INPUT
// ============================================================

void handleKey(char key) {
  lastPinActivity = millis();

  if (key == '*') {
    resetPin();
    clearResult();
    return;
  }

  if (pinLocked()) {
    if (key == '#') showResult(false, "PIN locked");
    return;
  }

  if (key == '#') {
    bool allowed =
      !pinOverflow &&
      scheduleAllows() &&
      usesPin(policy.mode) &&
      PinVerifier::verify(
        sharedKey,
        policy.id,
        policy.salt,
        policy.pinHash,
        enteredPin
      );

    // Revalidate expiry/time after HMAC verification.
    allowed = allowed && scheduleAllows();

    if (allowed) {
      failedPinAttempts = 0;
    }
    else if (++failedPinAttempts >= 5) {
      pinLockActive = true;
      pinLockStarted = millis();

      Serial.println("PIN locked for 30 seconds.");
    }

    showResult(allowed, "PIN");
  }
  else if (key >= '0' && key <= '9') {
    if (showingResult) clearResult();

    if (pinLength < 8 && !pinOverflow) {
      enteredPin[pinLength++] = key;
      enteredPin[pinLength] = '\0';
    }
    else {
      pinOverflow = true;
    }

    matrix.renderBitmap(neutral, 8, 12);
  }
}

void serviceKeypad() {
  keypad.tick();

  while (keypad.available()) {
    keypadEvent event = keypad.read();

    if (event.bit.EVENT == KEY_JUST_PRESSED) {
      handleKey((char)event.bit.KEY);
    }
  }
}

// ============================================================
// FINGERPRINT INPUT
// ============================================================

void serviceFingerprint() {
  if (!sensorReady ||
      solenoidIsActive() ||
      showingResult ||
      pinLength > 0 ||
      pinOverflow ||
      millis() - lastSensorPoll < 100) {
    return;
  }

  lastSensorPoll = millis();

  uint8_t present = fingerprint.detectFinger();

  if (present != 0 && present != 1) {
    sensorReady = false;
    showResult(false, "Fingerprint communication error");
    return;
  }

  if (waitingForFingerRelease) {
    if (present == 0) waitingForFingerRelease = false;
    return;
  }

  if (present == 0) return;

  waitingForFingerRelease = true;

  if (!scheduleAllows() ||
      !usesFingerprint(policy.mode)) {
    showResult(false, "Fingerprint");
    return;
  }

  if (fingerprint.collectionFingerprint(1) != 0) {
    showResult(false, "Fingerprint capture failed");
    return;
  }

  fingerprint.ctrlLED(
    fingerprint.eFastBlink,
    fingerprint.eLEDYellow,
    3
  );

  uint8_t id = fingerprint.search();

  bool allowed =
    id >= 1 &&
    id <= 80 &&
    id != ERR_ID809 &&
    scheduleAllows() &&
    usesFingerprint(policy.mode);

  showResult(allowed, "Fingerprint");
}

// ============================================================
// MANUAL TEST CLOCK
// ============================================================

void processTimeCommand() {
  serialInput[serialLength] = '\0';

  if (!serialOverflow &&
      strcmp(serialInput, "TIME CLEAR") == 0) {
    clockValid = false;
    Serial.println("Test clock invalidated.");
    return;
  }

  if (serialOverflow ||
      serialLength != 10 ||
      strncmp(serialInput, "TIME ", 5) != 0 ||
      serialInput[7] != ':' ||
      serialInput[5] < '0' ||
      serialInput[5] > '9' ||
      serialInput[6] < '0' ||
      serialInput[6] > '9' ||
      serialInput[8] < '0' ||
      serialInput[8] > '9' ||
      serialInput[9] < '0' ||
      serialInput[9] > '9') {
    Serial.println("Use TIME HH:MM or TIME CLEAR.");
    return;
  }

  int hour =
    (serialInput[5] - '0') * 10 +
    serialInput[6] - '0';

  int minute =
    (serialInput[8] - '0') * 10 +
    serialInput[9] - '0';

  if (hour > 23 || minute > 59) {
    Serial.println("Invalid test time.");
    return;
  }

  RTCTime time(
    1,
    Month::JANUARY,
    2026,
    hour,
    minute,
    0,
    DayOfWeek::THURSDAY,
    SaveLight::SAVING_TIME_INACTIVE
  );

  clockValid = RTC.setTime(time);

  Serial.println(
    clockValid
      ? "Manual test clock set; no timezone conversion."
      : "RTC setting failed."
  );
}

void serviceSerial() {
  while (Serial.available()) {
    serviceSolenoid();
    char c = Serial.read();

    if (c == '\r') continue;

    if (c == '\n') {
      if (serialLength || serialOverflow) {
        processTimeCommand();
        serviceSolenoidSafety();
      }

      clean(serialInput);
      serialLength = 0;
      serialOverflow = false;
    }
    else if (!serialOverflow &&
             serialLength < sizeof(serialInput) - 1) {
      serialInput[serialLength++] = c;
    }
    else {
      serialOverflow = true;
    }
  }
}

// ============================================================
// HTTP RESPONSE READING
// ============================================================

// Read one byte with a total transaction deadline.
// The byte counter also limits overall response size.

bool readWireByte(
  unsigned long started,
  size_t& wireCount,
  char& result
) {
  while (millis() - started < HTTP_DEADLINE_MS) {
    if (policyClient.available() > 0) {
      if (wireCount >= MAX_WIRE_BYTES) return false;

      int value = policyClient.read();

      if (value >= 0) {
        ++wireCount;
        result = (char)value;
        return true;
      }
    }
    else if (!policyClient.connected()) {
      return false;
    }

    delay(1);
  }

  return false;
}

// Read a CRLF-terminated HTTP line.

bool readHttpLine(
  char* line,
  size_t capacity,
  unsigned long started,
  size_t& wireCount
) {
  size_t length = 0;

  for (;;) {
    char c;

    if (!readWireByte(started, wireCount, c)) {
      return false;
    }

    if (c == '\n') {
      if (length == 0 || line[length - 1] != '\r') {
        return false;
      }

      line[length - 1] = '\0';
      return true;
    }

    if (length > 0 && line[length - 1] == '\r') {
      return false;
    }

    if (c == '\0' || length + 1 >= capacity) {
      return false;
    }

    line[length++] = c;
  }
}

// ASCII case-insensitive header name matching.

bool headerNameEquals(const char* a, const char* b) {
  while (*a && *b) {
    char x = *a++;
    char y = *b++;

    if (x >= 'A' && x <= 'Z') x += 'a' - 'A';
    if (y >= 'A' && y <= 'Z') y += 'a' - 'A';

    if (x != y) return false;
  }

  return !*a && !*b;
}

// Read exactly the requested number of body bytes.

bool readExact(
  uint8_t* destination,
  size_t count,
  unsigned long started,
  size_t& wireCount
) {
  size_t done = 0;

  while (done < count &&
         millis() - started < HTTP_DEADLINE_MS) {

    int available = policyClient.available();

    if (available > 0) {
      size_t take = (size_t)available;

      if (take > count - done) {
        take = count - done;
      }

      if (take > MAX_WIRE_BYTES - wireCount) {
        return false;
      }

      int received = policyClient.read(
        destination + done,
        take
      );

      if (received > 0) {
        done += (size_t)received;
        wireCount += (size_t)received;
      }
    }
    else if (!policyClient.connected()) {
      return false;
    }
    else {
      delay(1);
    }
  }

  return done == count;
}

// Validate Content-Length without integer overflow.

bool parseBodyLength(
  const char* value,
  size_t& result
) {
  if (!*value) return false;

  size_t number = 0;

  for (; *value; ++value) {
    if (*value < '0' || *value > '9') {
      return false;
    }

    size_t digit = (size_t)(*value - '0');

    if (digit > MAX_BODY_BYTES ||
        number > (MAX_BODY_BYTES - digit) / 10) {
      return false;
    }

    number = number * 10 + digit;
  }

  result = number;
  return true;
}

// ============================================================
// HTTP CHUNKED BODY SUPPORT
// ============================================================

// Supports ordinary chunked framing.
// Extensions and trailers are intentionally rejected.

bool readChunkedBody(
  char* body,
  size_t& length,
  unsigned long started,
  size_t& wireCount
) {
  char line[32] = {};
  length = 0;

  for (;;) {
    if (!readHttpLine(
          line,
          sizeof(line),
          started,
          wireCount
        )) {
      return false;
    }

    size_t chunk = 0;

    if (!line[0]) return false;

    for (const char* p = line; *p; ++p) {
      char c = *p;

      int digit =
        (c >= '0' && c <= '9') ? c - '0' :
        (c >= 'a' && c <= 'f') ? c - 'a' + 10 :
        (c >= 'A' && c <= 'F') ? c - 'A' + 10 :
        -1;

      if (digit < 0) return false;

      if ((size_t)digit > MAX_BODY_BYTES ||
          chunk >
            (MAX_BODY_BYTES - (size_t)digit) / 16) {
        return false;
      }

      chunk = chunk * 16 + (size_t)digit;
    }

    if (chunk == 0) {
      // Require final empty trailer line.
      if (!readHttpLine(
            line,
            sizeof(line),
            started,
            wireCount
          ) || line[0]) {
        return false;
      }

      return length > 0;
    }

    if (chunk > MAX_BODY_BYTES - length) {
      return false;
    }

    if (!readExact(
          (uint8_t*)body + length,
          chunk,
          started,
          wireCount
        )) {
      return false;
    }

    length += chunk;

    char cr;
    char lf;

    if (!readWireByte(started, wireCount, cr) ||
        cr != '\r' ||
        !readWireByte(started, wireCount, lf) ||
        lf != '\n') {
      return false;
    }
  }
}

// ============================================================
// CONNECTION-CLOSE BODY SUPPORT
// ============================================================

bool readCloseDelimitedBody(
  char* body,
  size_t& length,
  unsigned long started,
  size_t& wireCount
) {
  length = 0;

  while (millis() - started < HTTP_DEADLINE_MS) {
    int available = policyClient.available();

    if (available > 0) {
      if (length == MAX_BODY_BYTES) return false;

      size_t take = (size_t)available;

      if (take > MAX_BODY_BYTES - length) {
        take = MAX_BODY_BYTES - length;
      }

      if (take > MAX_WIRE_BYTES - wireCount) {
        return false;
      }

      int received = policyClient.read(
        (uint8_t*)body + length,
        take
      );

      if (received > 0) {
        length += (size_t)received;
        wireCount += (size_t)received;
      }
    }
    else if (!policyClient.connected()) {
      return length > 0;
    }
    else {
      delay(1);
    }
  }

  return false;
}

// ============================================================
// REMOTE POLICY FETCH
// ============================================================

bool fetchPolicy(
  RemotePolicy& candidate,
  bool& noActivePolicy
) {
  noActivePolicy = false;

  // Defensive guard: never enter blocking HTTP with D9 energised.
  lockSolenoid();

  if (WiFi.status() != WL_CONNECTED) {
    return false;
  }

  policyClient.stop();
  policyClient.setConnectionTimeout(3000);

  Serial.println("---- Policy poll ----");

  Serial.print("Arduino IP: ");
  Serial.println(WiFi.localIP());

  Serial.print("RSSI (dBm): ");
  Serial.println(WiFi.RSSI());

  Serial.print("Policy server: ");
  Serial.print(policyServer);
  Serial.print(":");
  Serial.println(POLICY_PORT);

  // --------------------------------------------------------
  // TCP CONNECTION
  // --------------------------------------------------------

  unsigned long started = millis();

  int connected = policyClient.connect(
    policyServer,
    POLICY_PORT
  );

  Serial.print("TCP result: ");
  Serial.println(connected);

  Serial.print("Connect duration (ms): ");
  Serial.println(millis() - started);

  if (connected != 1) {
    policyClient.stop();
    return false;
  }

  // --------------------------------------------------------
  // SINGLE-WRITE HTTP REQUEST
  // --------------------------------------------------------

  char request[192];

  int requestLength = snprintf(
    request,
    sizeof(request),
    "GET /api/policy/active HTTP/1.1\r\n"
    "Host: %s:%u\r\n"
    "Accept: application/json\r\n"
    "Accept-Encoding: identity\r\n"
    "Connection: close\r\n"
    "\r\n",
    POLICY_HOST,
    (unsigned int)POLICY_PORT
  );

  if (requestLength <= 0 ||
      requestLength >= (int)sizeof(request)) {
    Serial.println("HTTP request buffer too small.");
    policyClient.stop();
    return false;
  }

  size_t written = policyClient.write(
    (const uint8_t*)request,
    (size_t)requestLength
  );

  Serial.print("Request bytes written: ");
  Serial.print(written);
  Serial.print("/");
  Serial.println(requestLength);

  if (written != (size_t)requestLength) {
    Serial.println("Incomplete HTTP request write.");
    policyClient.stop();
    return false;
  }

  // --------------------------------------------------------
  // HTTP STATUS LINE
  // --------------------------------------------------------

  size_t wireCount = 0;
  char line[MAX_HEADER_LINE_BYTES] = {};

  if (!readHttpLine(
        line,
        sizeof(line),
        started,
        wireCount
      )) {
    Serial.println(
      "HTTP status line missing/invalid/timed out."
    );
    policyClient.stop();
    return false;
  }

  if ((strncmp(line, "HTTP/1.1 ", 9) &&
       strncmp(line, "HTTP/1.0 ", 9)) ||
      line[9] < '0' || line[9] > '9' ||
      line[10] < '0' || line[10] > '9' ||
      line[11] < '0' || line[11] > '9' ||
      (line[12] != '\0' && line[12] != ' ')) {

    Serial.println("Invalid HTTP status line.");
    policyClient.stop();
    return false;
  }

  int status =
    (line[9] - '0') * 100 +
    (line[10] - '0') * 10 +
    (line[11] - '0');

  Serial.print("HTTP response status: ");
  Serial.println(status);

  if (status == 204) {
    noActivePolicy = true;
    policyClient.stop();
    return false;
  }

  if (status != 200) {
    policyClient.stop();
    return false;
  }

  // --------------------------------------------------------
  // HTTP RESPONSE HEADERS
  // --------------------------------------------------------

  bool hasLength = false;
  bool chunked = false;
  bool hasTransferEncoding = false;

  size_t expectedLength = 0;
  uint8_t headerCount = 0;

  for (;;) {
    if (++headerCount > 32 ||
        wireCount > MAX_HEADER_BYTES ||
        !readHttpLine(
          line,
          sizeof(line),
          started,
          wireCount
        ) ||
        wireCount > MAX_HEADER_BYTES) {

      Serial.println(
        "HTTP headers invalid, oversized or timed out."
      );

      policyClient.stop();
      return false;
    }

    if (!line[0]) break;

    char* colon = strchr(line, ':');

    if (!colon || colon == line) {
      policyClient.stop();
      return false;
    }

    *colon = '\0';

    const char* value = colon + 1;

    while (*value == ' ' || *value == '\t') {
      ++value;
    }

    if (headerNameEquals(line, "Content-Length")) {
      if (hasLength ||
          !parseBodyLength(value, expectedLength)) {

        Serial.println(
          "Invalid/duplicate/oversized Content-Length."
        );

        policyClient.stop();
        return false;
      }

      hasLength = true;
    }
    else if (headerNameEquals(
               line,
               "Transfer-Encoding"
             )) {

      if (hasTransferEncoding ||
          !headerNameEquals(value, "chunked")) {

        Serial.println("Unsupported Transfer-Encoding.");
        policyClient.stop();
        return false;
      }

      hasTransferEncoding = true;
      chunked = true;
    }
    else if (headerNameEquals(
               line,
               "Content-Encoding"
             )) {

      if (!headerNameEquals(value, "identity")) {
        Serial.println(
          "Compressed policy responses unsupported."
        );

        policyClient.stop();
        return false;
      }
    }
  }

  if (hasLength && chunked) {
    Serial.println(
      "Ambiguous HTTP body framing rejected."
    );

    policyClient.stop();
    return false;
  }

  // --------------------------------------------------------
  // HTTP RESPONSE BODY
  // --------------------------------------------------------

  char body[MAX_BODY_BYTES + 1] = {};
  size_t length = 0;
  bool complete = false;

  if (chunked) {
    complete = readChunkedBody(
      body,
      length,
      started,
      wireCount
    );
  }
  else if (hasLength && expectedLength > 0) {
    complete = readExact(
      (uint8_t*)body,
      expectedLength,
      started,
      wireCount
    );

    if (complete) {
      length = expectedLength;
    }
  }
  else if (!hasLength) {
    complete = readCloseDelimitedBody(
      body,
      length,
      started,
      wireCount
    );
  }

  policyClient.stop();

  Serial.print("Body framing: ");
  Serial.println(
    chunked
      ? "chunked"
      : hasLength
        ? "content-length"
        : "connection-close"
  );

  Serial.print("Response bytes: ");
  Serial.println(length);

  Serial.print("Response complete: ");
  Serial.println(complete ? "YES" : "NO");

  if (!complete || !length) {
    return false;
  }

  // --------------------------------------------------------
  // POLICY VALIDATION
  // --------------------------------------------------------

  bool valid = parsePolicy(
    body,
    length,
    candidate
  );

  Serial.print("Policy validation: ");
  Serial.println(valid ? "PASS" : "FAIL");

  if (!valid) {
    Serial.println(
      "Policy schema, values or PIN keyId rejected."
    );
  }

  return valid;
}

// ============================================================
// POLICY POLLING SERVICE
// ============================================================

void servicePolicy() {
  serviceSolenoid();
  // Keep the existing bounded HTTP implementation, but defer it until
  // the pulse ends. Do not advance lastPolicyPoll while deferring.
  if (solenoidIsActive()) return;

  if (!firstPolicyPoll &&
      millis() - lastPolicyPoll < POLICY_POLL_MS) {
    return;
  }

  firstPolicyPoll = false;
  lastPolicyPoll = millis();

  if (!cryptoReady) return;

  if (WiFi.status() != WL_CONNECTED) {
    invalidatePolicy(
      "Wi-Fi unavailable; access denied."
    );

    wifiWasConnected = false;

    Serial.println("Wi-Fi reconnect attempt...");
    WiFi.begin(SECRET_SSID, SECRET_PASS);

    // Never fetch immediately after reconnection.
    return;
  }

  if (!wifiWasConnected) {
    wifiWasConnected = true;
    wifiConnectedAt = millis();

    Serial.println(
      "Wi-Fi restored; waiting before polling."
    );

    return;
  }

  if (millis() - wifiConnectedAt < WIFI_SETTLE_MS) {
    return;
  }

  RemotePolicy candidate = {};
  bool noActivePolicy = false;

  if (!fetchPolicy(candidate, noActivePolicy)) {
    invalidatePolicy(
      noActivePolicy
        ? "No active policy; access denied."
        : "Policy fetch/validation failed; access denied."
    );

    return;
  }

  bool changed =
    !policyReady ||
    !samePolicy(policy, candidate);

  policy = candidate;
  policyReceived = millis();
  policyReady = true;

  if (changed) {
    resetPin();
    clearResult();

    Serial.print("Active policy: ");
    Serial.print(policy.id);
    Serial.print(" version ");
    Serial.println(policy.version);
  }
}

// ============================================================
// SETUP
// ============================================================

void setup() {
  // First setup action, before serial, sensor, crypto or Wi-Fi work.
  initialiseSolenoid();

  Serial.begin(9600);

  matrix.begin();
  matrix.clear();

  keypad.begin();
  RTC.begin();

  fingerprint.begin();
  sensorReady = fingerprint.isConnected();
  blueRing();

  cryptoReady =
    PinVerifier::selfTest() &&
    PinVerifier::decodeHex(
      SHARED_KEY_HEX,
      sharedKey,
      sizeof(sharedKey),
      false
    );

  if (!cryptoReady) {
    clean(sharedKey);

    Serial.println(
      "PIN self-test/key configuration failed; access denied."
    );
  }
  else {
    Serial.println(
      "PIN interoperability self-test passed."
    );
  }

  WiFi.setTimeout(1500);

  // Numeric LAN IP rather than hostname/DNS.
  if (!policyServer.fromString(POLICY_HOST)) {
    Serial.println(
      "Invalid numeric POLICY_HOST; access denied."
    );

    cryptoReady = false;
  }
  else {
    // Explicitly establish Wi-Fi before policy fetching.
    Serial.println("Connecting to Wi-Fi SSID...");

    WiFi.begin(SECRET_SSID, SECRET_PASS);

    unsigned long start = millis();

    while (WiFi.status() != WL_CONNECTED &&
           millis() - start < WIFI_STARTUP_WAIT_MS) {
      delay(200);
    }

    if (WiFi.status() == WL_CONNECTED) {
      wifiWasConnected = true;
      wifiConnectedAt = millis();

      Serial.print("Connected. Arduino IP: ");
      Serial.println(WiFi.localIP());

      delay(WIFI_SETTLE_MS);
    }
    else {
      Serial.println(
        "Initial Wi-Fi connection failed; access denied."
      );
    }
  }

  Serial.println(
    "Remote policy bench: # submits PIN, * clears. "
    "PIN digits are hidden."
  );

  Serial.println(
    "Restricted times require TIME HH:MM after every reboot. "
    "TIME CLEAR invalidates it."
  );

  if (!sensorReady) {
    Serial.println(
      "Scanner unavailable; PIN may work "
      "when the active policy permits it."
    );
  }
}

// ============================================================
// MAIN LOOP
// ============================================================

void loop() {
  serviceSolenoidSafety();
  serviceSerial();
  serviceSolenoidSafety();

  if (policyReady &&
      millis() - policyReceived >= POLICY_MAX_AGE_MS) {
    invalidatePolicy(
      "Policy expired; access denied."
    );
  }

  if ((pinLength > 0 || pinOverflow) &&
      millis() - lastPinActivity >= PIN_TIMEOUT_MS) {
    resetPin();
    clearResult();
  }

  if (showingResult &&
      millis() - resultStarted >= DISPLAY_DURATION_MS) {
    clearResult();
  }

  pinLocked();
  servicePolicy();
  serviceSolenoidSafety();
  serviceKeypad();
  serviceSolenoidSafety();
  serviceFingerprint();
  serviceSolenoidSafety();

  delay(5);
}
