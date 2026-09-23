/**
  * @file    log.h
  * @brief   Minimal logging over raw UART (HAL_UART_Transmit), no printf.
  *
  *          Single-threaded test context only: no buffering, no locking.
  */
#ifndef __LOG_H__
#define __LOG_H__

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

/** Output a raw string without a trailing newline. */
void log_str(const char *s);

/**
  * @brief  Output a single character; '\n' is prefixed with '\r' (CRLF).
  */
void log_char(char c);

/** Output a string followed by "\r\n". */
void log_line(const char *s);

/**
  * @brief  Output "<label>0x<hex>\r\n".
  * @param  label  prefix text (may be NULL/empty)
  * @param  value  value to print in hex
  * @param  digits number of hex digits (1..8, zero-padded)
  */
void log_hex(const char *label, uint32_t value, uint8_t digits);

/** Output "[OK]   <what>\r\n". */
void log_pass(const char *what);

/** Output "[FAIL] <what>\r\n". */
void log_fail(const char *what);

#ifdef __cplusplus
}
#endif
#endif /* __LOG_H__ */
