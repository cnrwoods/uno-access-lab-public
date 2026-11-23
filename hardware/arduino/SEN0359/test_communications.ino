
#include <Wire.h>

void setup() {
  Serial.begin(115200);
  Wire.begin();

  delay(2000);
  Serial.println("Scanning I2C bus...");
}

void loop() {
  int devices = 0;

  for (byte address = 1; address < 127; address++) {
    Wire.beginTransmission(address);
    byte error = Wire.endTransmission();

    if (error == 0) {
      Serial.print("Device found at 0x");
      if (address < 16) Serial.print("0");
      Serial.println(address, HEX);
      devices++;
    }
  }

  if (devices == 0) {
    Serial.println("No I2C devices found.");
  }

  Serial.println("Scan complete.");
  delay(5000);
}
