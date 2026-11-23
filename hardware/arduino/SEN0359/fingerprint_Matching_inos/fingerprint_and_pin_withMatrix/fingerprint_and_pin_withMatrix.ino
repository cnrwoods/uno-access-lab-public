// UNO Access Lab: fingerprint OR PIN, with tick/cross feedback.
// Builds on fingerprint_Matching_withMatrix and the tested keypad wiring.
// Original DFRobot fingerprint example copyright (c) 2010 DFRobot Co.Ltd, MIT.
// Bench exercise: fixed demo PIN, no lock output or attempt lockout.
#include <DFRobot_ID809_I2C.h>
#include <Arduino_LED_Matrix.h>
#include <Adafruit_Keypad.h>
#include <string.h>

DFRobot_ID809_I2C fingerprint;
ArduinoLEDMatrix matrix;
const byte ROWS = 4, COLS = 3;
char keys[ROWS][COLS] = {
  {'1', '2', '3'}, {'4', '5', '6'}, {'7', '8', '9'}, {'*', '0', '#'}
};
// Rows: brown, red, orange, yellow. Columns: green, blue, purple.
byte rowPins[ROWS] = {8, 7, 6, 5};
byte colPins[COLS] = {4, 3, 2};
Adafruit_Keypad keypad(makeKeymap(keys), rowPins, colPins, ROWS, COLS);

const char VALID_PIN[] = "1234";
char enteredPin[sizeof(VALID_PIN)] = {}; // Four digits plus the string terminator.
byte pinLength = 0;
bool pinOverflow = false; // Extra digits invalidate the WHOLE attempt.
unsigned long lastPinActivity = 0;
const unsigned long PIN_TIMEOUT_MS = 30000;
const unsigned long DISPLAY_DURATION = 3000;
unsigned long resultStarted = 0;
bool showingResult = false;
const unsigned long DIGIT_DURATION_MS = 500;
unsigned long digitStarted = 0;
bool showingDigit = false;
// Five-bit rows for digits 0..9, using the keypad display example's font.
const byte digitGlyphs[10][7] = {
  {14,17,19,21,25,17,14}, {4,12,4,4,4,4,14},
  {14,17,1,2,4,8,31}, {30,1,1,14,1,1,30},
  {2,6,10,18,31,2,2}, {31,16,16,30,1,1,30},
  {14,16,16,30,17,17,14}, {31,1,2,4,8,8,8},
  {14,17,17,14,17,17,14}, {14,17,17,15,1,1,14}
};
byte digitFrame[8][12] = {};
bool sensorReady = false;
bool waitingForFingerRelease = false;
unsigned long lastSensorPoll = 0;
const unsigned long SENSOR_POLL_MS = 100;

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

// Cross symbol: 8 rows x 12 columns.
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


// =====================================================

void resetPin() {
  memset(enteredPin, 0, sizeof(enteredPin));
  pinLength = 0;
  pinOverflow = false;
}

void blueRing() {
  if (sensorReady) fingerprint.ctrlLED(fingerprint.eBreathing, fingerprint.eLEDBlue, 0);
}

void clearResult() {
  matrix.clear();
  showingResult = false;
  showingDigit = false;
  blueRing();
}

void showDigit(char key) {
  memset(digitFrame, 0, sizeof(digitFrame));
  for (byte row = 0; row < 7; row++) {
    for (byte col = 0; col < 5; col++) {
      digitFrame[row][col + 3] = (digitGlyphs[key - '0'][row] >> (4 - col)) & 1;
    }
  }
  matrix.renderBitmap(digitFrame, 8, 12);
  showingResult = false;
  showingDigit = true;
  digitStarted = millis(); // Restart the half-second timer on every press.
}

void showResult(bool allowed, const char* method) {
  resetPin(); // Never carry partial PIN digits into the next attempt.
  showingDigit = false; // An old digit timer must not erase the result.
  if (allowed) matrix.renderBitmap(tick, 8, 12);
  else matrix.renderBitmap(cross, 8, 12);
  if (sensorReady) {
    if (allowed) fingerprint.ctrlLED(fingerprint.eKeepsOn, fingerprint.eLEDGreen, 0);
    else fingerprint.ctrlLED(fingerprint.eKeepsOn, fingerprint.eLEDRed, 0);
  }
  resultStarted = millis();
  showingResult = true;
  Serial.print(method);
  Serial.println(allowed ? ": ALLOW" : ": DENY");
}

void handleKey(char key) {
  // Bench debugging: print each press, including PIN digits, * and #.
  Serial.print(key);
  Serial.println(" pressed");
  lastPinActivity = millis();
  if (key == '*') {
    resetPin();
    clearResult();
    Serial.println("PIN cleared.");
  } else if (key == '#') {
    bool allowed = !pinOverflow && pinLength == sizeof(VALID_PIN) - 1
                   && strcmp(enteredPin, VALID_PIN) == 0;
    showResult(allowed, "PIN");
  } else if (key >= '0' && key <= '9') {
    if (showingResult) clearResult();
    if (pinLength < sizeof(enteredPin) - 1 && !pinOverflow) {
      enteredPin[pinLength++] = key;
      enteredPin[pinLength] = '\0';
    } else {
      pinOverflow = true;
    }
    showDigit(key);
  }
}

void serviceKeypad() {
  keypad.tick();
  while (keypad.available()) {
    keypadEvent event = keypad.read();
    if (event.bit.EVENT == KEY_JUST_PRESSED) handleKey((char)event.bit.KEY);
  }
}

void serviceFingerprint() {
  // Give an in-progress PIN priority. No long wait for a finger to arrive.
  if (!sensorReady || showingResult || pinLength > 0 || pinOverflow) return;
  if (millis() - lastSensorPoll < SENSOR_POLL_MS) return;
  lastSensorPoll = millis();

  uint8_t present = fingerprint.detectFinger();
  if (present != 0 && present != 1) {
    sensorReady = false;
    showResult(false, "Fingerprint communication error");
    Serial.println("PIN remains available. Check sensor wiring and reset to retry sensor.");
    return;
  }
  if (waitingForFingerRelease) {
    if (present == 0) waitingForFingerRelease = false;
    return; // Holding a finger down must not authenticate repeatedly.
  }
  if (present == 0) return;

  waitingForFingerRelease = true;
  // These library calls are synchronous: keypad scanning briefly pauses during capture/search.
  // Only capture after detecting a finger, with a short acquisition timeout argument.
  if (fingerprint.collectionFingerprint(1) != 0) {
    showResult(false, "Fingerprint capture failed");
    return;
  }
  fingerprint.ctrlLED(fingerprint.eFastBlink, fingerprint.eLEDYellow, 3);
  uint8_t matchedId = fingerprint.search();
  // Only template IDs 1..80 count as a match; never accept an error value.
  bool allowed = matchedId >= 1 && matchedId <= 80 && matchedId != ERR_ID809;
  showResult(allowed, "Fingerprint");
  Serial.println("Lift your finger before trying again.");
}

void setup() {
  Serial.begin(9600);
  matrix.begin();
  matrix.clear();
  keypad.begin();
  fingerprint.begin();
  sensorReady = fingerprint.isConnected();
  blueRing();
  Serial.println("Ready: use a fingerprint OR enter a PIN and press #. * clears.");
  if (!sensorReady) Serial.println("Scanner unavailable: PIN still works. Reset after checking wiring.");
}

void loop() {
  serviceKeypad();
  if ((pinLength > 0 || pinOverflow) && millis() - lastPinActivity >= PIN_TIMEOUT_MS) {
    resetPin();
    Serial.println("Incomplete PIN cleared after 30 seconds.");
  }
  if (showingResult && millis() - resultStarted >= DISPLAY_DURATION) clearResult();
  if (showingDigit && millis() - digitStarted >= DIGIT_DURATION_MS) {
    matrix.clear();
    showingDigit = false; // Keep the entered PIN; only the picture expires.
  }
  serviceFingerprint();
  delay(5);
}
