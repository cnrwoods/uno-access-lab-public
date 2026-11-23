#pragma once

#include <Arduino.h>

const uint8_t SOLENOID_PIN = 9;
const unsigned long SOLENOID_UNLOCK_MS = 2000;

bool solenoidActive = false;
unsigned long unlockStarted = 0;

void initialiseSolenoid() {
  // Set the output latch before enabling the output driver.
  digitalWrite(SOLENOID_PIN, LOW);
  pinMode(SOLENOID_PIN, OUTPUT);
  solenoidActive = false;
  unlockStarted = 0;
}

void lockSolenoid() {
  digitalWrite(SOLENOID_PIN, LOW);
  solenoidActive = false;
}

bool solenoidIsActive() {
  return solenoidActive;
}

void unlockSolenoid() {
  // A PERMIT during an existing pulse must not restart its deadline.
  if (solenoidActive) return;

  unlockStarted = millis();
  solenoidActive = true;
  digitalWrite(SOLENOID_PIN, HIGH);
}

void serviceSolenoid() {
  // Unsigned subtraction also handles millis() rollover.
  if (solenoidActive &&
      millis() - unlockStarted >= SOLENOID_UNLOCK_MS) {
    lockSolenoid();
  }
}
