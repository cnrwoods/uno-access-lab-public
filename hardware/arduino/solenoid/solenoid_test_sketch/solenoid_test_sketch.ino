
const int SOLENOID_PIN = 9;
const unsigned long UNLOCK_DURATION = 2000;

bool solenoidActive = false;
unsigned long unlockStarted = 0;

void setup() {
  // Set output latch LOW before enabling the pin
  digitalWrite(SOLENOID_PIN, LOW);
  pinMode(SOLENOID_PIN, OUTPUT);

  Serial.begin(115200);

  Serial.println("Solenoid test ready.");
  Serial.println("D9 configured LOW (locked).");
  Serial.println("Send U to unlock for 2 seconds.");
}

void loop() {
  // Automatically deactivate after 2 seconds
  if (solenoidActive &&
      millis() - unlockStarted >= UNLOCK_DURATION) {
    digitalWrite(SOLENOID_PIN, LOW);
    solenoidActive = false;
    Serial.println("LOCKED: Solenoid OFF");
  }

  // Manual unlock command
  if (Serial.available() > 0) {
    char command = Serial.read();

    if (command == 'U' && !solenoidActive) {
      digitalWrite(SOLENOID_PIN, HIGH);
      solenoidActive = true;
      unlockStarted = millis();

      Serial.println("UNLOCKED: Solenoid ON");
    }
  }
}