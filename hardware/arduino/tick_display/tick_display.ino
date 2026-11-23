// Display a tick on the Arduino UNO R4 WiFi's built-in lights.
// This library comes with the Arduino UNO R4 Boards package.
#include <Arduino_LED_Matrix.h>

// Create a display controller and give it the name "matrix".
ArduinoLEDMatrix matrix;

// Our picture: 8 rows of 12 lights. 1 = on, 0 = off.
// "byte" stores a small whole number; this grid only uses 0 and 1.
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

// Arduino runs setup once each time the program starts.
void setup() {
  matrix.begin();                   // Start the display.
  matrix.renderBitmap(tick, 8, 12);  // Show our picture: 8 rows, 12 columns.
}

// Arduino repeatedly runs loop after setup has finished.
void loop() {
  // Nothing needs to change. The display library keeps the tick lit.
}
