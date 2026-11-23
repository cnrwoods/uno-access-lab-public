
#include <WiFiS3.h>

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println("UNO R4 WiFi - Network Scan");

  // Check that the Wi-Fi module is responding.
  if (WiFi.status() == WL_NO_MODULE) {
    Serial.println("ERROR: Wi-Fi module not detected!");
    while (true);
  }

  Serial.print("Firmware version: ");
  Serial.println(WiFi.firmwareVersion());
}

void loop() {
  Serial.println();
  Serial.println("Scanning for Wi-Fi networks...");

  int networks = WiFi.scanNetworks();

  if (networks < 0) {
    Serial.println("Wi-Fi scan failed.");
  } else {
    Serial.print("Networks found: ");
    Serial.println(networks);

    for (int i = 0; i < networks; i++) {
      Serial.print(i + 1);
      Serial.print(". SSID: ");
      Serial.print(WiFi.SSID(i));

      Serial.print(" | Signal (dBm): ");
      Serial.print(WiFi.RSSI(i));

      Serial.print(" | Encryption: ");
      Serial.println(WiFi.encryptionType(i));
    }
  }

  delay(10000);
}