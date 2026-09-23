/**
  * @file    log.c
  * @brief   Minimal logging over raw UART (HAL_UART_Transmit), no printf.
  */
#include "log.h"
#include "usart.h"
#include <string.h>

/** TX buffer used to assemble hex output before a single UART send. */
#define LOG_TX_MAX  16u

static const char kCRLF[] = "\r\n";
static const char kHex[]  = "0123456789ABCDEF";

static void log_uart(const uint8_t *data, uint16_t len)
{
    (void)HAL_UART_Transmit(&huart1, (uint8_t *)data, len, HAL_MAX_DELAY);
}

void log_str(const char *s)
{
    if (s != NULL)
    {
        log_uart((const uint8_t *)s, (uint16_t)strlen(s));
    }
}

void log_char(char c)
{
    static const uint8_t kCR = (uint8_t)'\r';

    if (c == '\n')
    {
        log_uart(&kCR, 1u);
    }
    log_uart((const uint8_t *)&c, 1u);
}

void log_line(const char *s)
{
    log_str(s);
    log_uart((const uint8_t *)kCRLF, 2u);
}

void log_hex(const char *label, uint32_t value, uint8_t digits)
{
    uint8_t  buf[LOG_TX_MAX];
    uint16_t pos = 0u;

    if (digits == 0u || digits > 8u)
    {
        digits = 8u;
    }

    if (label != NULL)
    {
        size_t l = strlen(label);
        if (l > (size_t)(LOG_TX_MAX - digits - 2u - 2u))
        {
            l = (size_t)(LOG_TX_MAX - digits - 2u - 2u);
        }
        (void)memcpy(buf, label, l);
        pos = (uint16_t)l;
    }

    buf[pos++] = '0';
    buf[pos++] = 'x';

    /* Most-significant nibble first; shift grows with position. */
    for (uint8_t shift = digits; shift > 0u; shift--)
    {
        buf[pos++] = (uint8_t)kHex[(value >> ((shift - 1u) * 4u)) & 0xFu];
    }

    log_uart(buf, pos);
    log_uart((const uint8_t *)kCRLF, 2u);
}

void log_pass(const char *what)
{
    log_str("[OK]   ");
    log_line(what);
}

void log_fail(const char *what)
{
    log_str("[FAIL] ");
    log_line(what);
}
