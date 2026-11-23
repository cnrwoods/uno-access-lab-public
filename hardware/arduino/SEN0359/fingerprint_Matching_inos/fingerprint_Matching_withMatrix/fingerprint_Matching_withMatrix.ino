
/*!
 * @file fingerprint_Matching.ino
 * @brief Fingerprint authentication with UNO R4 WiFi LED matrix feedback
 *
 * Project: Fingerprint Entry
 *
 * Hardware:
 * - Arduino UNO R4 WiFi
 * - DFRobot SEN0359 / ID809 fingerprint sensor (I2C)
 *
 * Functionality:
 * - Capture fingerprints using the ID809 sensor
 * - Search enrolled fingerprint templates
 * - Display a tick on successful matching
 * - Display a cross on unsuccessful matching
 * - Preserve original DFRobot sensor LED behaviour
 * - Clear the display after 3 seconds
 *
 * Based on the DFRobot fingerprintMatching example.
 * Original copyright (c) 2010 DFRobot Co.Ltd
 * Original licence: MIT
 */

#include <DFRobot_ID809_I2C.h>
#include <Arduino_LED_Matrix.h>

// =====================================================
// GLOBAL OBJECTS
// =====================================================

// Fingerprint sensor controller.
DFRobot_ID809_I2C fingerprint;

// Arduino UNO R4 WiFi built-in LED matrix controller.
ArduinoLEDMatrix matrix;

// Duration to show authentication results (milliseconds).
const unsigned long DISPLAY_DURATION = 3000;


// =====================================================
// LED MATRIX BITMAPS
// =====================================================

// Tick symbol: 8 rows x 12 columns.
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
// LED MATRIX FUNCTIONS
// =====================================================

// Display a tick for a successful fingerprint match.
void showTick() {
  matrix.renderBitmap(tick, 8, 12);
}

// Display a cross for an unsuccessful fingerprint match.
void showCross() {
  matrix.renderBitmap(cross, 8, 12);
}

// Clear all LEDs on the matrix.
void clearDisplay() {
  matrix.clear();
}


// =====================================================
// FINGERPRINT HELPER FUNCTIONS
// =====================================================

// Wait until the user has removed their finger.
void waitForFingerRelease() {

  Serial.println("Please release your finger");

  // detectFinger() returns true while a finger is present.
  while (fingerprint.detectFinger()) {
    delay(20);
  }
}


// =====================================================
// SETUP - RUNS ONCE
// =====================================================

void setup() {

  // Initialise USB serial communication.
  Serial.begin(9600);

  // Allow Serial Monitor time to connect.
  delay(2000);

  Serial.println("==============================");
  Serial.println("Fingerprint Entry");
  Serial.println("Arduino UNO R4 WiFi");
  Serial.println("==============================");

  // Initialise the built-in LED matrix.
  Serial.println("Initialising LED matrix...");

  if (matrix.begin()) {
    Serial.println("LED matrix initialised");
  } else {
    Serial.println("ERROR: LED matrix initialisation failed");
  }

  // Start with the LED matrix blank.
  clearDisplay();

  // Initialise the ID809 fingerprint module.
  Serial.println("Initialising fingerprint scanner...");

  fingerprint.begin();

  // Retry until the scanner responds.
  while (!fingerprint.isConnected()) {
    Serial.println("Communication with device failed");
    Serial.println("Please check wiring");
    delay(1000);
  }

  Serial.println("Fingerprint scanner connected");

  // Display how many fingerprints are enrolled.
  Serial.print("Enrolled fingerprints: ");
  Serial.println(fingerprint.getEnrollCount());

  Serial.println("System ready");
  Serial.println("==============================");
}


// =====================================================
// MAIN LOOP - RUNS CONTINUOUSLY
// =====================================================

void loop() {

  // Stores the matched fingerprint ID.
  // Zero means no matching fingerprint was found.
  uint8_t ret = 0;

  // ---------------------------------------------------
  // STAGE 1: WAIT FOR FINGER
  // ---------------------------------------------------

  // Set the fingerprint scanner LED ring to breathing blue.
  fingerprint.ctrlLED(
    fingerprint.eBreathing,
    fingerprint.eLEDBlue,
    0
  );

  Serial.println("Please press down your finger");

  // ---------------------------------------------------
  // STAGE 2: CAPTURE FINGERPRINT
  // ---------------------------------------------------

  // Attempt to capture a fingerprint.
  // Timeout: 10 seconds.
  // ERR_ID809 indicates capture failure.

  if (fingerprint.collectionFingerprint(10) == ERR_ID809) {

    Serial.println("Capturing fails");

    // Wait for finger release before retrying.
    waitForFingerRelease();

    Serial.println("-----------------------------");

    // Return to the beginning of loop().
    // No matching is performed on a failed capture.
    return;
  }

  // ---------------------------------------------------
  // STAGE 3: SUCCESSFUL CAPTURE - YELLOW LED
  // ---------------------------------------------------

  Serial.println("Capturing succeeds");

  // Restore the original DFRobot behaviour:
  // Flash the fingerprint scanner LED yellow 3 times.
  fingerprint.ctrlLED(
    fingerprint.eFastBlink,
    fingerprint.eLEDYellow,
    3
  );

  // Ask the user to release their finger.
  waitForFingerRelease();

  // ---------------------------------------------------
  // STAGE 4: MATCH FINGERPRINT
  // ---------------------------------------------------

  // Compare captured fingerprint with all enrolled
  // fingerprint templates stored on the ID809 module.
  //
  // Returns fingerprint ID (1-80) on success.
  // Returns 0 if no match is found.

  ret = fingerprint.search();

  // ---------------------------------------------------
  // STAGE 5A: SUCCESSFUL MATCH
  // ---------------------------------------------------

  if (ret != 0) {

    // Turn the fingerprint scanner LED solid green.
    fingerprint.ctrlLED(
      fingerprint.eKeepsOn,
      fingerprint.eLEDGreen,
      0
    );

    // Display a tick on the Arduino LED matrix.
    showTick();

    // Print the successfully matched fingerprint ID.
    Serial.print("Matching succeeds,ID=");
    Serial.println(ret);

    Serial.println("Authentication: SUCCESS");

  }

  // ---------------------------------------------------
  // STAGE 5B: UNSUCCESSFUL MATCH
  // ---------------------------------------------------

  else {

    // Turn the fingerprint scanner LED solid red.
    fingerprint.ctrlLED(
      fingerprint.eKeepsOn,
      fingerprint.eLEDRed,
      0
    );

    // Display a cross on the Arduino LED matrix.
    showCross();

    Serial.println("Matching fails");
    Serial.println("Authentication: FAILED");
  }

  // ---------------------------------------------------
  // STAGE 6: DISPLAY RESULT AND RESET
  // ---------------------------------------------------

  Serial.println("-----------------------------");

  // Keep green/red LED and tick/cross displayed
  // for 3 seconds.
  delay(DISPLAY_DURATION);

  // Clear the Arduino LED matrix.
  clearDisplay();

  // The next iteration of loop() will:
  // - Restore the blue breathing LED
  // - Wait for another fingerprint
  // - Repeat the authentication process
}