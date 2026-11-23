// UNO R4 WiFi + Adafruit 419 membrane keypad.
// Displays the most recently pressed key until another key is pressed.
// Learning exercise only: this prints/displays keys, not a secure PIN entry UI.
#include <Adafruit_Keypad.h>
#include <Arduino_LED_Matrix.h>

const byte ROWS = 4;
const byte COLS = 3;
char keys[ROWS][COLS] = {
  {'1', '2', '3'},
  {'4', '5', '6'},
  {'7', '8', '9'},
  {'*', '0', '#'}
};

// Your tested wiring: connector 1 (rightmost) -> D2, through 7 -> D8.
// Rows: brown, red, orange, yellow. Columns: green, blue, purple.
byte rowPins[ROWS] = {8, 7, 6, 5};
byte colPins[COLS] = {4, 3, 2};
Adafruit_Keypad keypad(makeKeymap(keys), rowPins, colPins, ROWS, COLS);
ArduinoLEDMatrix matrix;

// Each character is five lights wide and seven lights tall.
// Each number below holds one row of five on/off bits.
// For example, 0b01110 means: off, on, on, on, off.
const char glyphCharacters[] = "0123456789*#";
const byte glyphs[12][7] = {
  {0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110}, // 0
  {0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110}, // 1
  {0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111}, // 2
  {0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110}, // 3
  {0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010}, // 4
  {0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110}, // 5
  {0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110}, // 6
  {0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000}, // 7
  {0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110}, // 8
  {0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110}, // 9
  {0b00000, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00000}, // *
  {0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010}  // #
};

byte frame[8][12] = {}; // The full matrix: initially all lights off.

void showKey(char key) {
  int glyphIndex = -1;
  for (byte i = 0; i < 12; i++) {
    if (glyphCharacters[i] == key) {
      glyphIndex = i;
      break;
    }
  }
  if (glyphIndex < 0) return;

  // Clear the previous character before drawing the new one.
  for (byte row = 0; row < 8; row++) {
    for (byte col = 0; col < 12; col++) {
      frame[row][col] = 0;
    }
  }

  // Put the five-column character near the centre of the 12-column matrix.
  for (byte row = 0; row < 7; row++) {
    for (byte col = 0; col < 5; col++) {
      frame[row][col + 3] = (glyphs[glyphIndex][row] >> (4 - col)) & 1;
    }
  }
  matrix.renderBitmap(frame, 8, 12);
}

void setup() {
  Serial.begin(9600);
  matrix.begin();
  matrix.clear();
  keypad.begin();
  // No wait for Serial: the display also works with Serial Monitor closed.
  Serial.println("Ready. Press a keypad button to display it on the matrix.");
}

void loop() {
  keypad.tick(); // Scan the keypad and update its button events.

  while (keypad.available()) {
    keypadEvent event = keypad.read();
    char key = (char)event.bit.KEY;

    if (event.bit.EVENT == KEY_JUST_PRESSED) {
      Serial.print(key);
      Serial.println(" pressed");
      showKey(key);
    } else if (event.bit.EVENT == KEY_JUST_RELEASED) {
      Serial.print(key);
      Serial.println(" released");
      // Leave the character visible after the button is released.
    }
  }
  delay(10); // Same short scan interval as the Adafruit keypad example.
}
