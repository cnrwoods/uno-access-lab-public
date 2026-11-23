#pragma once

#include <stdint.h>

// Pure calendar arithmetic: no libc localtime/mktime or Arduino global clock.
// The RTC stores UTC; schedules are interpreted in Europe/London using the
// current UK rule (last Sunday in March/October, both at 01:00 UTC).
// Rule source: https://www.gov.uk/when-do-the-clocks-change
namespace RemoteClock {

constexpr int MIN_YEAR = 2024;
constexpr int MAX_YEAR = 2099; // The board RTC has a two-digit year field.
constexpr uint32_t SECONDS_PER_DAY = 86400UL;

struct UtcDateTime {
  int year;
  int month; // 1..12, unlike the Arduino RTC Month enum.
  int day;
  int hour;
  int minute;
  int second;
};

constexpr bool isLeapYear(int year) {
  return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

constexpr int daysInMonth(int year, int month) {
  if (month < 1 || month > 12) return 0;
  if (month == 2) return isLeapYear(year) ? 29 : 28;
  return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
}

constexpr bool validUtc(const UtcDateTime& value) {
  return value.year >= MIN_YEAR && value.year <= MAX_YEAR &&
      value.month >= 1 && value.month <= 12 && value.day >= 1 &&
      value.day <= daysInMonth(value.year, value.month) &&
      value.hour >= 0 && value.hour < 24 &&
      value.minute >= 0 && value.minute < 60 &&
      value.second >= 0 && value.second < 60;
}

constexpr bool toUnixSeconds(const UtcDateTime& value, uint32_t& result) {
  if (!validUtc(value)) return false;
  uint32_t days = 0;
  for (int year = 1970; year < value.year; ++year) {
    days += isLeapYear(year) ? 366 : 365;
  }
  for (int month = 1; month < value.month; ++month) {
    days += static_cast<uint32_t>(daysInMonth(value.year, month));
  }
  days += static_cast<uint32_t>(value.day - 1);
  result = days * SECONDS_PER_DAY + static_cast<uint32_t>(value.hour) * 3600UL +
      static_cast<uint32_t>(value.minute) * 60UL +
      static_cast<uint32_t>(value.second);
  return true;
}

constexpr bool fromUnixSeconds(uint32_t epoch, UtcDateTime& result) {
  uint32_t days = epoch / SECONDS_PER_DAY;
  int year = 1970;
  while (year <= MAX_YEAR) {
    const uint32_t yearDays = isLeapYear(year) ? 366 : 365;
    if (days < yearDays) break;
    days -= yearDays;
    ++year;
  }
  if (year < MIN_YEAR || year > MAX_YEAR) return false;
  int month = 1;
  while (month <= 12) {
    const uint32_t monthDays = static_cast<uint32_t>(daysInMonth(year, month));
    if (days < monthDays) break;
    days -= monthDays;
    ++month;
  }
  const uint32_t daytime = epoch % SECONDS_PER_DAY;
  const UtcDateTime value{year, month, static_cast<int>(days) + 1,
      static_cast<int>(daytime / 3600UL),
      static_cast<int>((daytime / 60UL) % 60UL),
      static_cast<int>(daytime % 60UL)};
  if (!validUtc(value)) return false;
  result = value;
  return true;
}

// The Arduino RTC's DayOfWeek enum also uses Sunday=0.
constexpr uint8_t weekdaySundayZero(const UtcDateTime& value) {
  uint32_t epoch = 0;
  if (!toUnixSeconds(value, epoch)) return 0xFF;
  return static_cast<uint8_t>((epoch / SECONDS_PER_DAY + 4UL) % 7UL);
}

constexpr int lastSunday(int year, int month) {
  const UtcDateTime end{year, month, daysInMonth(year, month), 0, 0, 0};
  const uint8_t weekday = weekdaySundayZero(end);
  return weekday == 0xFF ? 0 : end.day - weekday;
}

constexpr bool britishSummerTime(const UtcDateTime& utc) {
  if (!validUtc(utc) || utc.month < 3 || utc.month > 10) return false;
  if (utc.month > 3 && utc.month < 10) return true;
  const int transition = lastSunday(utc.year, utc.month);
  if (utc.month == 3) {
    return utc.day > transition || (utc.day == transition && utc.hour >= 1);
  }
  return utc.day < transition || (utc.day == transition && utc.hour < 1);
}

constexpr bool londonMinuteOfDay(const UtcDateTime& utc, uint16_t& minute) {
  if (!validUtc(utc)) return false;
  minute = static_cast<uint16_t>((utc.hour * 60 + utc.minute +
      (britishSummerTime(utc) ? 60 : 0)) % 1440);
  return true;
}

struct SyncState {
  bool valid = false;
  uint32_t syncedAtMillis = 0;
  uint32_t syncedUtcEpoch = 0;

  constexpr void clear() {
    valid = false;
    syncedAtMillis = 0;
    syncedUtcEpoch = 0;
  }

  constexpr bool markSynced(uint32_t nowMillis, uint32_t epoch) {
    UtcDateTime value{};
    if (!fromUnixSeconds(epoch, value)) {
      clear();
      return false;
    }
    syncedAtMillis = nowMillis;
    syncedUtcEpoch = epoch;
    valid = true;
    return true;
  }

  // Call regularly from the main loop. Expiry latches invalid so crossing a
  // later millis() rollover cannot make an already expired clock valid again.
  constexpr bool isFresh(uint32_t nowMillis, uint32_t maxAgeMs) {
    if (!valid) return false;
    if (maxAgeMs == 0 ||
        static_cast<uint32_t>(nowMillis - syncedAtMillis) >= maxAgeMs) {
      valid = false;
    }
    return valid;
  }

  // Detect a stopped/jumped RTC against monotonic time since the trusted sync.
  // Caller should apply isFresh() first and clear state if this returns false.
  constexpr bool plausibleProgress(const UtcDateTime& utc, uint32_t nowMillis,
                                   uint32_t toleranceSeconds = 120) const {
    uint32_t actual = 0;
    if (!valid || !toUnixSeconds(utc, actual)) return false;
    const uint64_t expected = static_cast<uint64_t>(syncedUtcEpoch) +
        static_cast<uint32_t>(nowMillis - syncedAtMillis) / 1000UL;
    const uint64_t difference = actual >= expected ? actual - expected : expected - actual;
    return difference <= toleranceSeconds;
  }
};

} // namespace RemoteClock
