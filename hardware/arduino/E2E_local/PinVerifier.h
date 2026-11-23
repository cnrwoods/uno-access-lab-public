#pragma once

#include <Crypto.h>
#include <SHA256.h>
#include <stdint.h>
#include <string.h>

// Matches PolicyApi.PinProtection byte for byte. Nothing here decrypts a PIN.
namespace PinVerifier {

inline int hexDigit(char value, bool lowerOnly) {
  if (value >= '0' && value <= '9') return value - '0';
  if (value >= 'a' && value <= 'f') return value - 'a' + 10;
  if (!lowerOnly && value >= 'A' && value <= 'F') return value - 'A' + 10;
  return -1;
}

inline bool decodeHex(const char* text, uint8_t* output, size_t bytes, bool lowerOnly = true) {
  if (!text || strlen(text) != bytes * 2) return false;
  for (size_t index = 0; index < bytes; ++index) {
    int high = hexDigit(text[index * 2], lowerOnly);
    int low = hexDigit(text[index * 2 + 1], lowerOnly);
    if (high < 0 || low < 0) return false;
    output[index] = (uint8_t)((high << 4) | low);
  }
  return true;
}

inline bool validPin(const char* pin) {
  if (!pin) return false;
  size_t length = strlen(pin);
  if (length < 4 || length > 8) return false;
  for (size_t index = 0; index < length; ++index)
    if (pin[index] < '0' || pin[index] > '9') return false;
  return true;
}

inline bool matchesKeyId(const uint8_t key[32], const char* keyId) {
  uint8_t expected[8] = {}, digest[32] = {};
  if (!decodeHex(keyId, expected, sizeof(expected))) return false;
  SHA256 sha;
  sha.update(key, 32);
  sha.finalize(digest, sizeof(digest));
  bool matches = secure_compare(digest, expected, sizeof(expected));
  sha.clear();
  clean(digest);
  clean(expected);
  return matches;
}

inline bool verify(const uint8_t key[32], const char* policyId, const char salt[33],
                   const uint8_t expected[32], const char* pin) {
  if (!validPin(pin)) return false;
  const char domain[] = "uno-access-lab/pin/v1\n";
  const char separator = '\n';
  uint8_t actual[32] = {};
  SHA256 sha;
  sha.resetHMAC(key, 32);
  sha.update(domain, sizeof(domain) - 1);
  sha.update(policyId, strlen(policyId));
  sha.update(&separator, 1);
  sha.update(salt, 32); // Lowercase hexadecimal TEXT, not decoded salt bytes.
  sha.update(&separator, 1);
  sha.update(pin, strlen(pin)); // Preserve leading zeroes; no trailing NUL/newline.
  sha.finalizeHMAC(key, 32, actual, sizeof(actual));
  bool matches = secure_compare(actual, expected, sizeof(actual));
  sha.clear();
  clean(actual);
  return matches;
}

inline bool selfTest() {
  // Public interoperability vector, never the configured registration key.
  uint8_t key[32] = {}, expected[32] = {};
  for (uint8_t index = 0; index < sizeof(key); ++index) key[index] = index;
  bool decoded = decodeHex("ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f",
                           expected, sizeof(expected));
  bool passed = decoded && matchesKeyId(key, "630dcd2966c43366") &&
    verify(key, "POL-001", "000102030405060708090a0b0c0d0e0f", expected, "0007") &&
    !verify(key, "POL-001", "000102030405060708090a0b0c0d0e0f", expected, "0008");
  clean(key);
  clean(expected);
  return passed;
}

} // namespace PinVerifier
