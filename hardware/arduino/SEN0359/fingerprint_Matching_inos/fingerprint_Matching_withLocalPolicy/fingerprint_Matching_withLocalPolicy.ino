
/*!
 * @file fingerprint_Matching_withLocalPolicy.ino
 * @brief Fingerprint matching with local time-based access policy
 *
 * Project: Fingerprint Entry
 *
 * Hardware:
 * - Arduino UNO R4 WiFi
 * - DFRobot SEN0359 / ID809 fingerprint sensor
 *
 * Features:
 * - Biometric matching using the ID809
 * - Local even/odd minute access policy
 * - RTC-based time evaluation
 * - Manual test clock via Serial Monitor
 * - Tick/cross on UNO R4 LED matrix
 * - Original fingerprint scanner LED behaviour
 *
 * Based on DFRobot fingerprintMatching example (MIT).
 */

#include <DFRobot_ID809_I2C.h>
#include <Arduino_LED_Matrix.h>
#include <RTC.h>

// =====================================================
// 1. HARDWARE OBJECTS
// =====================================================

DFRobot_ID809_I2C fingerprint;
ArduinoLEDMatrix matrix;


// =====================================================
// 2. ACCESS CONTROL POLICY
// =====================================================

// Defines the structure of an access policy.
// Later, these values can come from an Azure API.
struct AccessPolicy {
  bool enabled;
  uint8_t allowedMinuteParity;
};

// Our first policy.
// 0 = even minutes
// 1 = odd minutes
AccessPolicy policy = {
  true,
  0
};

// The test clock becomes valid after being initialised.
// In production, validity must depend on trusted time sync.
bool clockValid = false;


// =====================================================
// 3. CONFIGURATION
// =====================================================

// Authentication result display duration.
const unsigned long DISPLAY_DURATION = 3000;

// Prevent repeated "press finger" messages.
bool waitingPromptShown = false;

// Track whether the blue ready LED has been configured.
bool readyLightSet = false;

// Serial input buffer for setting test time (HH:MM).
char timeInput[6];
uint8_t timeInputLength = 0;


// =====================================================
// 4. LED MATRIX BITMAPS
// =====================================================

// Successful access: tick.
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

// Access denied: cross.
uint8_t cross[8][12] = {
  {0,1,1,0,0,0,0,0,0,1,1,0},
  {0,0,1,1,0,0,0,0,1,1,0,0},
  {0,0,0,1,1,0,0,1,1,0,0,0},
  {0,0,0,1,1,0,0,1,1,0,0,0},
  {0,0,0,1,1,0,0,1,1,0,0,0},
  {0,0,1,1,0,0,0,0,1,1,0,0},
  {0,1,1,0,0,0,0,0,0,1,1,0},
  {0,1,1,0,0,0,0,0,0,1,1,0}
};

void showTick() {
  matrix.renderBitmap(tick, 8, 12);
}

void showCross() {
  matrix.renderBitmap(cross, 8, 12);
}

void clearDisplay() {
  matrix.clear();
}


// =====================================================
// 5. TEST CLOCK MANAGEMENT
// =====================================================

// Sets the RTC to a chosen time.
// The date is arbitrary for this local demonstration.
void setTestClock(int hour, int minute) {

  RTCTime testTime(
    18,
    Month::OCTOBER,
    2026,
    hour,
    minute,
    0,
    DayOfWeek::SUNDAY,
    SaveLight::SAVING_TIME_INACTIVE
  );

  RTC.setTime(testTime);
  clockValid = true;

  Serial.print("Test clock set to ");
  printTime();
}

// Print current RTC time.
void printTime() {

  RTCTime currentTime;
  RTC.getTime(currentTime);

  if (currentTime.getHour() < 10) Serial.print("0");
  Serial.print(currentTime.getHour());
  Serial.print(":");

  if (currentTime.getMinutes() < 10) Serial.print("0");
  Serial.print(currentTime.getMinutes());
  Serial.print(":");

  if (currentTime.getSeconds() < 10) Serial.print("0");
  Serial.println(currentTime.getSeconds());
}


// =====================================================
// 6. SERIAL TEST COMMANDS
// =====================================================

// Accepts HH:MM through Serial Monitor.
// Example: 13:32 or 13:33.
void processTimeCommand(const char* input) {

  // Check the expected HH:MM format.
  if (
    input[0] < '0' || input[0] > '9' ||
    input[1] < '0' || input[1] > '9' ||
    input[2] != ':' ||
    input[3] < '0' || input[3] > '9' ||
    input[4] < '0' || input[4] > '9' ||
    input[5] != '\0'
  ) {
    Serial.println("Invalid time. Use HH:MM");
    return;
  }

  int hour =
    (input[0] - '0') * 10 + (input[1] - '0');

  int minute =
    (input[3] - '0') * 10 + (input[4] - '0');

  // Validate time range.
  if (hour > 23 || minute > 59) {
    Serial.println("Invalid time range");
    return;
  }

  setTestClock(hour, minute);
}

// Read Serial Monitor without waiting for a command.
void handleSerialInput() {

  while (Serial.available() > 0) {

    char incoming = Serial.read();

    // Ignore carriage returns.
    if (incoming == '\r') {
      continue;
    }

    // Newline means the command is complete.
    if (incoming == '\n') {

      timeInput[timeInputLength] = '\0';

      if (timeInputLength > 0) {
        processTimeCommand(timeInput);
      }

      timeInputLength = 0;
      continue;
    }

    // Add character if there is buffer space.
    if (timeInputLength < 5) {
      timeInput[timeInputLength++] = incoming;
    } else {
      Serial.println("Invalid command length");
      timeInputLength = 0;
    }
  }
}


// =====================================================
// 7. POLICY EVALUATION
// =====================================================

// Evaluate fingerprint identity against local policy.
//
// Returns true if access should be permitted.
// Returns false if access should be denied.
bool evaluateAccess(uint8_t fingerprintId, int minute) {

  // Policy disabled means access denied.
  if (!policy.enabled) {
    Serial.println("DENY: Policy disabled");
    return false;
  }

  // A valid clock is required.
  if (!clockValid) {
    Serial.println("DENY: Clock not synchronised");
    return false;
  }

  // No successful fingerprint match.
  if (fingerprintId == 0) {
    Serial.println("DENY: Fingerprint not recognised");
    return false;
  }

  // Check even/odd minute rule.
  if ((minute % 2) != policy.allowedMinuteParity) {
    Serial.println("DENY: Minute not permitted by policy");
    return false;
  }

  // All conditions satisfied.
  Serial.println("PERMIT: All policy conditions satisfied");
  return true;
}


// =====================================================
// 8. FINGERPRINT HELPER
// =====================================================

void waitForFingerRelease() {

  Serial.println("Please release your finger");

  while (fingerprint.detectFinger()) {
    delay(20);
  }
}


// =====================================================
// 9. SETUP
// =====================================================

void setup() {

  Serial.begin(9600);
  delay(2000);

  Serial.println("============================");
  Serial.println("FINGERPRINT ENTRY");
  Serial.println("LOCAL POLICY TEST");
  Serial.println("============================");

  // Initialise UNO R4 LED matrix.
  if (matrix.begin()) {
    Serial.println("LED matrix initialised");
  } else {
    Serial.println("ERROR: LED matrix failed");
  }

  clearDisplay();

  // Initialise built-in RTC.
  RTC.begin();

  // Start at a known time for local testing.
  // This resets to 13:32 on every Arduino restart.
  setTestClock(13, 32);

  // Initialise fingerprint sensor.
  fingerprint.begin();

  while (!fingerprint.isConnected()) {
    Serial.println("Fingerprint connection failed");
    delay(1000);
  }

  Serial.println("Fingerprint scanner connected");

  Serial.print("Enrolled fingerprints: ");
  Serial.println(fingerprint.getEnrollCount());

  Serial.println("----------------------------");
  Serial.println("ACTIVE POLICY:");
  Serial.println("Fingerprint match required");

  if (policy.allowedMinuteParity == 0) {
    Serial.println("Allowed minutes: EVEN");
  } else {
    Serial.println("Allowed minutes: ODD");
  }

  Serial.println("Set test time using HH:MM");
  Serial.println("Examples: 13:32 or 13:33");
  Serial.println("============================");
}


// =====================================================
// 10. MAIN LOOP
// =====================================================

void loop() {

  // Process any incoming Serial test commands.
  handleSerialInput();

  // Set blue breathing LED once per ready cycle.
  if (!readyLightSet) {

    fingerprint.ctrlLED(
      fingerprint.eBreathing,
      fingerprint.eLEDBlue,
      0
    );

    readyLightSet = true;
  }

  // Prompt once while waiting for a finger.
  if (!waitingPromptShown) {
    Serial.println("Please press down your finger");
    waitingPromptShown = true;
  }

  // No finger detected: return to start of loop.
  // This lets us process Serial commands while idle.
  if (!fingerprint.detectFinger()) {
    delay(50);
    return;
  }

  // Finger detected, begin capture.
  // Reset state for the next attempt.
  waitingPromptShown = false;
  readyLightSet = false;

  // Capture fingerprint.
  if (fingerprint.collectionFingerprint(10) == ERR_ID809) {

    Serial.println("Capturing fails");

    waitForFingerRelease();

    Serial.println("----------------------------");
    return;
  }

  // Fingerprint captured successfully.
  Serial.println("Capturing succeeds");

  // Original yellow capture feedback.
  fingerprint.ctrlLED(
    fingerprint.eFastBlink,
    fingerprint.eLEDYellow,
    3
  );

  // Wait until finger is released.
  waitForFingerRelease();

  // Search enrolled fingerprint templates.
  uint8_t fingerprintId = fingerprint.search();

  // Record biometric matching result separately
  // from our authorisation decision.
  if (fingerprintId != 0) {

    Serial.print("BIOMETRIC MATCH: ID=");
    Serial.println(fingerprintId);

  } else {
    Serial.println("BIOMETRIC MATCH: FAILED");
  }

  // Read the current time at the decision point.
  RTCTime currentTime;
  RTC.getTime(currentTime);

  int minute = currentTime.getMinutes();

  Serial.print("Current time: ");
  printTime();

  Serial.print("Minute parity: ");
  Serial.println(
    minute % 2 == 0 ? "EVEN" : "ODD"
  );

  // Evaluate against our local access policy.
  bool accessPermitted =
    evaluateAccess(fingerprintId, minute);

  // Apply final access decision.
  if (accessPermitted) {

    // Green sensor LED.
    fingerprint.ctrlLED(
      fingerprint.eKeepsOn,
      fingerprint.eLEDGreen,
      0
    );

    // Tick on Arduino LED matrix.
    showTick();

    Serial.println("ACCESS DECISION: PERMIT");

  } else {

    // Red sensor LED.
    fingerprint.ctrlLED(
      fingerprint.eKeepsOn,
      fingerprint.eLEDRed,
      0
    );

    // Cross on Arduino LED matrix.
    showCross();

    Serial.println("ACCESS DECISION: DENY");
  }

  Serial.println("----------------------------");

  // Display the result for 3 seconds.
  delay(DISPLAY_DURATION);

  // Clear the matrix for the next attempt.
  clearDisplay();
}