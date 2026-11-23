
#include <DFRobot_ID809_I2C.h>

DFRobot_ID809_I2C fingerprint;

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println("Initialising fingerprint sensor...");

  fingerprint.begin();

  if (fingerprint.isConnected()) {
    Serial.println("Fingerprint sensor connected!");

    Serial.print("Module ID: ");
    Serial.println(fingerprint.getDeviceID());

    Serial.print("Enrolled fingerprints: ");
    Serial.println(fingerprint.getEnrollCount());
  } else {
    Serial.println("Fingerprint sensor communication failed.");
  }
}

void loop() {
  delay(1000);
}
