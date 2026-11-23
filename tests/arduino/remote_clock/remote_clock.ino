#include "../../../hardware/arduino/E2E_remote/RemoteClock.h"

// These are compile-time assertions on the real firmware helpers. No board
// upload, Wi-Fi credentials or host compiler installation is required.
using namespace RemoteClock;

static_assert(isLeapYear(2000) && isLeapYear(2024), "Leap-year rule");
static_assert(!isLeapYear(1900) && !isLeapYear(2026), "Non-leap-year rule");
static_assert(validUtc({2024, 2, 29, 23, 59, 59}), "Valid leap day");
static_assert(!validUtc({2026, 2, 29, 0, 0, 0}), "Reject invalid leap day");
static_assert(!validUtc({2026, 4, 31, 0, 0, 0}), "Reject invalid month length");
static_assert(!validUtc({2026, 0, 1, 0, 0, 0}), "Reject zero month");
static_assert(!validUtc({2026, 13, 1, 0, 0, 0}), "Reject month overflow");
static_assert(!validUtc({2026, 1, 0, 0, 0, 0}), "Reject zero day");
static_assert(!validUtc({2026, 1, 1, 24, 0, 0}), "Reject hour overflow");
static_assert(!validUtc({2026, 1, 1, 0, 60, 0}), "Reject minute overflow");
static_assert(!validUtc({2026, 1, 1, 0, 0, 60}), "Reject leap second");
static_assert(!validUtc({2023, 12, 31, 23, 59, 59}), "Reject old clock baseline");
static_assert(!validUtc({2100, 1, 1, 0, 0, 0}), "Reject unsupported RTC century");
static_assert(weekdaySundayZero({2026, 3, 29, 12, 0, 0}) == 0, "Sunday");
static_assert(weekdaySundayZero({2024, 1, 1, 0, 0, 0}) == 1, "Monday");
static_assert(lastSunday(2026, 3) == 29 && lastSunday(2026, 10) == 25, "2026 transitions");
static_assert(lastSunday(2027, 3) == 28 && lastSunday(2027, 10) == 31, "2027 transitions");
static_assert(lastSunday(2028, 3) == 26 && lastSunday(2028, 10) == 29, "2028 transitions");
static_assert(!britishSummerTime({2026, 3, 29, 0, 59, 59}), "Spring before transition");
static_assert(britishSummerTime({2026, 3, 29, 1, 0, 0}), "Spring exact transition");
static_assert(britishSummerTime({2026, 10, 25, 0, 59, 59}), "Autumn before transition");
static_assert(!britishSummerTime({2026, 10, 25, 1, 0, 0}), "Autumn exact transition");
static_assert(!britishSummerTime({2026, 1, 1, 12, 0, 0}), "GMT winter");
static_assert(britishSummerTime({2026, 7, 1, 12, 0, 0}), "BST summer");

constexpr bool epochExamples() {
  uint32_t epoch = 0;
  UtcDateTime result{};
  if (!toUnixSeconds({2024, 1, 1, 0, 0, 0}, epoch) || epoch != 1704067200UL) return false;
  if (!toUnixSeconds({2024, 2, 29, 12, 34, 56}, epoch) || epoch != 1709210096UL) return false;
  if (!toUnixSeconds({2026, 3, 29, 1, 0, 0}, epoch) || epoch != 1774746000UL) return false;
  if (!fromUnixSeconds(1792890000UL, result) || result.year != 2026 ||
      result.month != 10 || result.day != 25 || result.hour != 1 ||
      result.minute != 0 || result.second != 0) return false;
  if (!toUnixSeconds({2099, 12, 31, 23, 59, 59}, epoch) || epoch != 4102444799UL) return false;
  if (!fromUnixSeconds(epoch, result) || result.year != 2099 || result.month != 12 ||
      result.day != 31 || result.hour != 23 || result.minute != 59 || result.second != 59) return false;
  if (fromUnixSeconds(0, result) || fromUnixSeconds(4102444800UL, result)) return false;
  return true;
}
static_assert(epochExamples(), "Known UTC epoch examples and range checks");

constexpr bool londonExamples() {
  uint16_t minute = 9999;
  if (!londonMinuteOfDay({2026, 3, 29, 0, 59, 59}, minute) || minute != 59) return false;
  if (!londonMinuteOfDay({2026, 3, 29, 1, 0, 0}, minute) || minute != 120) return false;
  if (!londonMinuteOfDay({2026, 10, 25, 0, 59, 59}, minute) || minute != 119) return false;
  if (!londonMinuteOfDay({2026, 10, 25, 1, 0, 0}, minute) || minute != 60) return false;
  if (!londonMinuteOfDay({2026, 7, 1, 23, 30, 0}, minute) || minute != 30) return false;
  return !londonMinuteOfDay({2026, 2, 30, 12, 0, 0}, minute);
}
static_assert(londonExamples(), "GMT/BST minute boundaries and midnight wrap");

constexpr bool freshnessExamples() {
  SyncState state{};
  if (state.isFresh(0, 1000)) return false;
  if (!state.markSynced(0xFFFFFF00UL, 1774746000UL)) return false;
  if (!state.isFresh(0x000000F4UL, 1000)) return false; // 500 ms across rollover.
  if (state.isFresh(0x000002E8UL, 1000)) return false; // Exact 1,000 ms expiry.
  if (state.isFresh(0xFFFFFF01UL, 1000)) return false; // Invalid stays latched.
  if (!state.markSynced(1000, 1774746000UL) || state.isFresh(1000, 0)) return false;
  if (!state.markSynced(1000, 1774746000UL)) return false;
  state.clear();
  if (state.valid || state.isFresh(1001, 1000)) return false;
  if (state.markSynced(0, 0) || state.valid) return false;
  return true;
}
static_assert(freshnessExamples(), "Freshness expiry, rollover and explicit invalidation");

constexpr bool progressExamples() {
  SyncState state{};
  if (!state.markSynced(1000, 1774746000UL)) return false;
  if (!state.plausibleProgress({2026, 3, 29, 1, 1, 0}, 61000, 0)) return false;
  if (state.plausibleProgress({2026, 3, 29, 1, 0, 0}, 61000, 5)) return false;
  if (state.plausibleProgress({2026, 3, 29, 2, 0, 0}, 61000, 120)) return false;
  if (!state.markSynced(0xFFFFFF00UL, 1774746000UL)) return false;
  return state.plausibleProgress({2026, 3, 29, 1, 0, 1}, 0x000002E8UL, 0);
}
static_assert(progressExamples(), "RTC stop/jump checks and monotonic rollover");

void setup() {}
void loop() {}
