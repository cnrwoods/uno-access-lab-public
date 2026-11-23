// A USB-controlled display exercise for the Arduino UNO R4 WiFi.
// This demonstrates decisions; it does not authenticate anyone or unlock a door.
#include <Arduino_LED_Matrix.h>

ArduinoLEDMatrix matrix;

// Pictures contain 8 rows of 12 lights: 1 = on, 0 = off.
byte tick[8][12] = {
  {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
  {0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0},
  {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0},
  {0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0},
  {0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0},
  {0, 0, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0},
  {0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0},
  {0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0}
};

byte cross[8][12] = {
  {0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0},
  {0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0},
  {0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0},
  {0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0},
  {0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0},
  {0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0},
  {0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0},
  {0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0}
};

const unsigned long displayDurationMs = 3000; // 3000 milliseconds = 3 seconds.
unsigned long displayStartedAt = 0;
bool showingResult = false; // A bool stores either true or false.

String command = ""; // Collect the text arriving over USB.
const unsigned int maxCommandLength = 32;
bool commandTooLong = false;

void clearDisplay() {
  matrix.clear();
  showingResult = false;
}

void handleCommand() {
  // == compares values. A single = assigns a value.
  if (!commandTooLong && command == "allow") {
    matrix.renderBitmap(tick, 8, 12);
    Serial.println("ALLOW: showing tick for 3 seconds.");
  } else if (!commandTooLong && command == "deny") {
    matrix.renderBitmap(cross, 8, 12);
    Serial.println("DENY: showing cross for 3 seconds.");
  } else {
    // Remove any previous result so an invalid command cannot leave a tick lit.
    clearDisplay();
    Serial.println("Unknown command. Type allow or deny in lowercase.");
    return; // Finish this function now.
  }

  displayStartedAt = millis(); // Read the board's running clock.
  showingResult = true;
}

void setup() {
  Serial.begin(115200); // Start text communication with the PC.
  command.reserve(maxCommandLength); // Set aside space for incoming text.
  matrix.begin();
  clearDisplay(); // Start with a blank display, not an access result.
  Serial.println("Ready. Send allow or deny, with Newline selected.");
}

void loop() {
  // Check the timer without pausing the program. Input still works meanwhile.
  if (showingResult && millis() - displayStartedAt >= displayDurationMs) {
    clearDisplay();
    Serial.println("Ready: display cleared.");
  }

  // Read one character at a time so the timer is checked frequently.
  if (Serial.available() > 0) {
    char incoming = Serial.read();

    if (incoming == '\n') { // Newline means the command is complete.
      handleCommand();
      command = "";
      commandTooLong = false;
    } else if (incoming != '\r') { // Ignore the extra character in CR+LF mode.
      if (command.length() < maxCommandLength && !commandTooLong) {
        command += incoming; // Add this character to the text collected so far.
      } else {
        commandTooLong = true; // Reject the entire overlong line at Newline.
      }
    }
  }
}
