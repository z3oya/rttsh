/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Minimal UART-only demo: echo bytes back over USART1.
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Includes ------------------------------------------------------------------*/
#include "main.h"
#include "usart.h"
#include "gpio.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */
#include <errno.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include "log.h"
#include "SEGGER_RTT.h"
/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */
typedef void (*cmd_handler_t)(int argc, char *argv[]);

typedef struct {
  const char *name;
  cmd_handler_t handler;
  const char *help;   /* one-line usage, printed by 'help' and on arg errors */
} cmd_entry_t;

typedef struct {
  const char *name;    /* "r" / "g" / "b" */
  GPIO_TypeDef *port;
  uint16_t pin;
} led_map_t;

typedef struct {
  bool     pending;   /* set until the done print; new requests are rejected while set */
  uint32_t id;        /* per-slot accepted counter, echoed on accept and on done */
  uint32_t start;     /* HAL_GetTick() snapshot at accept */
  uint32_t due;       /* accepted delay in ms */
} rtt_async_slot_t;

/* Forward declarations: s_cmd_table (PV) references the handlers, so these
   must precede it; the PFP section sits after PV in the CubeMX layout. */
static void rtt_cmd_poll(void);
static void rtt_line_feed(uint8_t c);
static void cmd_execute_line(void);
static uint32_t cmd_tokenize(char *line, char *argv[], uint32_t max_argc);
static const cmd_entry_t *cmd_lookup(const char *name);
static void cmd_help(int argc, char *argv[]);
static const led_map_t *led_lookup(const char *name);
static void cmd_led(int argc, char *argv[]);
static void cmd_tick(int argc, char *argv[]);
static void cmd_reboot(int argc, char *argv[]);
static bool cmd_parse_ms(const char *arg, uint32_t *out_ms);
static bool rtt_async_claim(rtt_async_slot_t *slot, uint32_t ms);
static void rtt_async_cancel(rtt_async_slot_t *slot, const char *name);
static void rtt_async_report(const rtt_async_slot_t *slot, const char *name);
static void cmd_async1(int argc, char *argv[]);
static void cmd_async2(int argc, char *argv[]);
static void cmd_async_status(int argc, char *argv[]);
static void cmd_echo(int argc, char *argv[]);
static void rtt_tick_task(void);
static void rtt_async_task(void);
/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */
#define RTT_PROMPT         "rtt> "   /* shell prompt */
#define RTT_CMD_LINE_MAX   64u       /* line buffer: command + args + NUL */
#define RTT_CMD_MAX_ARGS   8u        /* tokens kept; argv array holds one more NULL sentinel */
#define RTT_POLL_PERIOD_MS 10u       /* main loop idle cadence */
#define RTT_TICK_PERIOD_MS 1000u     /* periodic tick print period */
#define RTT_DLY_DEFAULT_MS 1000u     /* async1/async2 delay when no argument is given */
#define RTT_ASYNC_SLOT_1   0u        /* s_async[] index of the 'async1' slot */
#define RTT_ASYNC_SLOT_2   1u        /* s_async[] index of the 'async2' slot */
#define RTT_ASYNC_SLOT_COUNT 2u
/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/

/* USER CODE BEGIN PV */
static const char s_led_usage[] = "led <r|g|b> [on|off|toggle] : drive or query LED";
static const char s_tick_usage[] = "tick [on|off] : show or set 1 s print";
static const char s_async1_usage[] = "async1 [ms|cancel] : reply now and again at timeout (default 1000)";
static const char s_async2_usage[] = "async2 [ms|quiet|cancel] : silent accept, result at timeout only (default 1000)";
static const char s_async_status_usage[] = "async : show both async channels' state";
static const char s_echo_usage[] = "echo <text> [text...] : echo tokens joined by single spaces";

static const cmd_entry_t s_cmd_table[] = {
  {"help",   cmd_help,   "list commands"},
  {"?",      cmd_help,   "alias of help"},
  {"led",    cmd_led,    s_led_usage},
  {"tick",   cmd_tick,   s_tick_usage},
  {"reboot", cmd_reboot, "reset MCU"},
  {"async",  cmd_async_status, s_async_status_usage},
  {"async1", cmd_async1, s_async1_usage},
  {"async2", cmd_async2, s_async2_usage},
  {"echo",   cmd_echo,   s_echo_usage},
  {NULL, NULL, NULL}
};
static const led_map_t s_led_table[] = {
  {"r", LED_R_GPIO_Port, LED_R_Pin},
  {"g", LED_G_GPIO_Port, LED_G_Pin},
  {"b", LED_B_GPIO_Port, LED_B_Pin},   /* LED_B lives on GPIOA, unlike R and G */
  {NULL, NULL, 0}
};
static char     s_line[RTT_CMD_LINE_MAX];   /* in-progress input line */
static uint16_t s_line_len;
static bool     s_cr_last;                  /* true = last byte was a submit CR: swallow the LF of CRLF */
static bool     s_tick_enabled = false;      /* quiet by default; enable with 'tick on' */
static uint32_t s_seconds;    /* uptime counter for the tick print */
static uint32_t s_tick_ref;   /* HAL_GetTick() snapshot of the last 1 s expiry */
static rtt_async_slot_t s_async[RTT_ASYNC_SLOT_COUNT];   /* [SLOT_1]=async1, [SLOT_2]=async2 */
static const char * const s_async_name[RTT_ASYNC_SLOT_COUNT] = {"async1", "async2"};
/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
static void MPU_Config(void);
/* USER CODE BEGIN PFP */

/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */

/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MPU Configuration--------------------------------------------------------*/
  MPU_Config();

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_USART1_UART_Init();
  /* USER CODE BEGIN 2 */
  log_line("");
  log_line("=== basic-jlink-rtt : UART TX-only ===");
  log_line("USART1 115200 8N1, transmit only.");

  /* SEGGER RTT: output goes to the J-Link RTT Viewer/Console on channel 0. */
  SEGGER_RTT_ConfigUpBuffer(0, NULL, NULL, 0, SEGGER_RTT_MODE_NO_BLOCK_SKIP);
  SEGGER_RTT_printf(0, "\r\n=== basic-jlink-rtt : RTT ===\r\n");
  SEGGER_RTT_printf(0, "RTT channel 0 ready. Output also mirrored on USART1.\r\n");
  s_tick_ref = HAL_GetTick();
  SEGGER_RTT_printf(0, "Type 'help' for commands.\r\n");
  SEGGER_RTT_printf(0, RTT_PROMPT);
  /* USER CODE END 2 */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    rtt_cmd_poll();
    rtt_tick_task();
    rtt_async_task();
    
    if (SEGGER_RTT_HasData(0) == 0u)
    {
      HAL_Delay(RTT_POLL_PERIOD_MS);   /* idle cadence; busy-poll while the host streams data */
    }
    /* USER CODE END WHILE */

    /* USER CODE BEGIN 3 */
  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  RCC_OscInitTypeDef RCC_OscInitStruct = {0};
  RCC_ClkInitTypeDef RCC_ClkInitStruct = {0};

  /** Supply configuration update enable
  */
  HAL_PWREx_ConfigSupply(PWR_LDO_SUPPLY);

  /** Configure the main internal regulator output voltage
  */
  __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE0);

  while(!__HAL_PWR_GET_FLAG(PWR_FLAG_VOSRDY)) {}

  /** Initializes the RCC Oscillators according to the specified parameters
  * in the RCC_OscInitTypeDef structure.
  */
  RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSE;
  RCC_OscInitStruct.HSEState = RCC_HSE_ON;
  RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
  RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSE;
  RCC_OscInitStruct.PLL.PLLM = 5;
  RCC_OscInitStruct.PLL.PLLN = 180;
  RCC_OscInitStruct.PLL.PLLP = 2;
  RCC_OscInitStruct.PLL.PLLQ = 4;
  RCC_OscInitStruct.PLL.PLLR = 2;
  RCC_OscInitStruct.PLL.PLLRGE = RCC_PLL1VCIRANGE_2;
  RCC_OscInitStruct.PLL.PLLVCOSEL = RCC_PLL1VCOWIDE;
  RCC_OscInitStruct.PLL.PLLFRACN = 0;
  if (HAL_RCC_OscConfig(&RCC_OscInitStruct) != HAL_OK)
  {
    Error_Handler();
  }

  /** Initializes the CPU, AHB and APB buses clocks
  */
  RCC_ClkInitStruct.ClockType = RCC_CLOCKTYPE_HCLK|RCC_CLOCKTYPE_SYSCLK
                              |RCC_CLOCKTYPE_PCLK1|RCC_CLOCKTYPE_PCLK2
                              |RCC_CLOCKTYPE_D3PCLK1|RCC_CLOCKTYPE_D1PCLK1;
  RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
  RCC_ClkInitStruct.SYSCLKDivider = RCC_SYSCLK_DIV1;
  RCC_ClkInitStruct.AHBCLKDivider = RCC_HCLK_DIV2;
  RCC_ClkInitStruct.APB3CLKDivider = RCC_APB3_DIV2;
  RCC_ClkInitStruct.APB1CLKDivider = RCC_APB1_DIV2;
  RCC_ClkInitStruct.APB2CLKDivider = RCC_APB2_DIV2;
  RCC_ClkInitStruct.APB4CLKDivider = RCC_APB4_DIV2;

  if (HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_3) != HAL_OK)
  {
    Error_Handler();
  }
}

/* USER CODE BEGIN 4 */
/* ---- RTT input: drain the down buffer, line editing with echo ---- */

/**
  * @brief  Drain pending RTT input bytes into the line editor.
  * @retval None
  */
static void rtt_cmd_poll(void)
{
  uint8_t buf[16u];   /* matches BUFFER_SIZE_DOWN: drains the ring in one pass */
  unsigned n;
  unsigned i;

  do
  {
    n = SEGGER_RTT_Read(0u, buf, sizeof(buf));
    for (i = 0u; i < n; i++)
    {
      rtt_line_feed(buf[i]);
    }
  } while (n > 0u);
}

/**
  * @brief  Feed one input byte: printable echo, backspace, submit on CR/LF.
  * @param  c raw byte from the RTT down buffer
  * @retval None
  */
static void rtt_line_feed(uint8_t c)
{
  if ((c == '\r') || (c == '\n'))
  {
    /* Swallow the LF of a CRLF pair, but only as the byte right after the CR. */
    if ((c == '\n') && s_cr_last && (s_line_len == 0u))
    {
      s_cr_last = false;
      return;
    }
    s_cr_last = (c == '\r') ? true : false;
    SEGGER_RTT_WriteString(0, "\r\n");
    cmd_execute_line();   /* uses s_line/s_line_len, then the line is spent */
    s_line_len = 0u;
    SEGGER_RTT_WriteString(0, RTT_PROMPT);
  }
  else if ((c == '\b') || (c == 0x7Fu))
  {
    s_cr_last = false;
    if (s_line_len > 0u)
    {
      s_line_len--;
      SEGGER_RTT_WriteString(0, "\b \b");
    }
  }
  else if ((c >= 0x20u) && (c <= 0x7Eu))
  {
    s_cr_last = false;
    if (s_line_len < (RTT_CMD_LINE_MAX - 1u))
    {
      s_line[s_line_len] = (char)c;
      s_line_len++;
      SEGGER_RTT_PutChar(0, (char)c);
    }
    /* line full: drop silently, backspace still works */
  }
  else
  {
    /* other control or non-ASCII bytes: ignore */
    s_cr_last = false;
  }
}

/* ---- command parsing and dispatch ---- */

/**
  * @brief  Tokenize a line in place into argv; tokens beyond max_argc are dropped.
  * @param  line     NUL-terminated line, modified in place
  * @param  argv     output array of max_argc + 1 entries; argv[argc] = NULL
  * @param  max_argc maximum number of tokens collected
  * @retval token count
  */
static uint32_t cmd_tokenize(char *line, char *argv[], uint32_t max_argc)
{
  uint32_t argc = 0u;
  char *p = line;

  while (*p != '\0')
  {
    while ((*p == ' ') || (*p == '\t'))
    {
      p++;
    }
    if (*p == '\0')
    {
      break;
    }
    if (argc < max_argc)
    {
      argv[argc] = p;
      argc++;
    }
    while ((*p != '\0') && (*p != ' ') && (*p != '\t'))
    {
      p++;
    }
    if (*p != '\0')
    {
      *p = '\0';
      p++;
    }
  }
  argv[argc] = NULL;
  return argc;
}

/**
  * @brief  Find a command table entry by name.
  * @param  name first token of the submitted line
  * @retval matching entry, or NULL
  */
static const cmd_entry_t *cmd_lookup(const char *name)
{
  const cmd_entry_t *cmd;

  for (cmd = s_cmd_table; cmd->name != NULL; cmd++)
  {
    if (strcmp(name, cmd->name) == 0)
    {
      return cmd;
    }
  }
  return NULL;
}

/**
  * @brief  Parse s_line and run the matching command handler.
  * @retval None
  */
static void cmd_execute_line(void)
{
  char *argv[RTT_CMD_MAX_ARGS + 1u];
  uint32_t argc;
  const cmd_entry_t *cmd;

  s_line[s_line_len] = '\0';
  argc = cmd_tokenize(s_line, argv, RTT_CMD_MAX_ARGS);
  if (argc == 0u)
  {
    return;   /* empty line: the caller prints the prompt */
  }

  cmd = cmd_lookup(argv[0]);
  if (cmd != NULL)
  {
    cmd->handler((int)argc, argv);
  }
  else
  {
    SEGGER_RTT_printf(0, "cmd: '%s' not found, try 'help'\r\n", argv[0]);
  }
}

/**
  * @brief  List all commands with their one-line help.
  * @retval None
  */
static void cmd_help(int argc, char *argv[])
{
  const cmd_entry_t *cmd;

  (void)argc;
  (void)argv;
  for (cmd = s_cmd_table; cmd->name != NULL; cmd++)
  {
    SEGGER_RTT_printf(0, "%s - %s\r\n", cmd->name, cmd->help);
  }
}

/**
  * @brief  Find an LED table entry by name.
  * @param  name LED selector: "r" / "g" / "b"
  * @retval matching entry, or NULL
  */
static const led_map_t *led_lookup(const char *name)
{
  const led_map_t *led;

  for (led = s_led_table; led->name != NULL; led++)
  {
    if (strcmp(name, led->name) == 0)
    {
      return led;
    }
  }
  return NULL;
}

/**
  * @brief  Drive one board LED: led <r|g|b> <on|off|toggle>.
  * @retval None
  */
static void cmd_led(int argc, char *argv[])
{
  const led_map_t *led;

  if ((argc != 2) && (argc != 3))
  {
    SEGGER_RTT_printf(0, "usage: %s\r\n", s_led_usage);
    return;
  }

  led = led_lookup(argv[1]);
  if (led == NULL)
  {
    SEGGER_RTT_printf(0, "unknown LED '%s'\r\n", argv[1]);
    return;
  }

  if (argc == 2)   /* query current state (output mode: IDR mirrors the pin) */
  {
    GPIO_PinState state = HAL_GPIO_ReadPin(led->port, led->pin);
    SEGGER_RTT_printf(0, "LED %s %s\r\n", led->name,
                      (state == GPIO_PIN_RESET) ? "on" : "off");   /* active-low */
    return;
  }

  /* Board LEDs are active-low: on = RESET, off = SET. */
  if (strcmp(argv[2], "on") == 0)
  {
    HAL_GPIO_WritePin(led->port, led->pin, GPIO_PIN_RESET);
    SEGGER_RTT_printf(0, "LED %s on\r\n", led->name);
  }
  else if (strcmp(argv[2], "off") == 0)
  {
    HAL_GPIO_WritePin(led->port, led->pin, GPIO_PIN_SET);
    SEGGER_RTT_printf(0, "LED %s off\r\n", led->name);
  }
  else if (strcmp(argv[2], "toggle") == 0)
  {
    HAL_GPIO_TogglePin(led->port, led->pin);
    SEGGER_RTT_printf(0, "LED %s toggled\r\n", led->name);
  }
  else
  {
    SEGGER_RTT_printf(0, "unknown action '%s'\r\n", argv[2]);
  }
}

/**
  * @brief  Show or set the periodic tick print: tick [on|off].
  * @retval None
  */
static void cmd_tick(int argc, char *argv[])
{
  if (argc == 1)
  {
    SEGGER_RTT_printf(0, "tick: %s\r\n", s_tick_enabled ? "on" : "off");
  }
  else if ((argc == 2) && (strcmp(argv[1], "on") == 0))
  {
    s_tick_enabled = true;
    SEGGER_RTT_printf(0, "tick on\r\n");
  }
  else if ((argc == 2) && (strcmp(argv[1], "off") == 0))
  {
    s_tick_enabled = false;
    SEGGER_RTT_printf(0, "tick off\r\n");
  }
  else
  {
    SEGGER_RTT_printf(0, "usage: %s\r\n", s_tick_usage);
  }
}

/**
  * @brief  Reset the MCU after the host drained the up buffer.
  * @retval None
  */
static void cmd_reboot(int argc, char *argv[])
{
  uint32_t start;

  (void)argc;
  (void)argv;
  SEGGER_RTT_printf(0, "rebooting...\r\n");
  /* A reset rebuilds the RTT control block and unread up-buffer data is lost;
     wait until the host drained it (bounded, an absent host cannot hang us). */
  start = HAL_GetTick();
  while ((SEGGER_RTT_HasDataUp(0u) != 0u) && ((HAL_GetTick() - start) < 500u))
  {
  }
  NVIC_SystemReset();
}

/* ---- timing-test commands: async1/async2 reply later from the main loop ---- */

/**
  * @brief  Parse a decimal millisecond argument for async1/async2.
  * @param  arg token from argv
  * @param  out_ms parsed value, written only on success
  * @retval true if arg is a plain decimal number that fits in int32 ms
  */
static bool cmd_parse_ms(const char *arg, uint32_t *out_ms)
{
  char *end;
  long val;

  errno = 0;
  val = strtol(arg, &end, 10);
  /* errno rejects out-of-range input (strtol saturates at LONG_MAX otherwise);
     that caps ms at ~24.8 days, far beyond any console use. */
  if ((errno != 0) || (end == arg) || (*end != '\0') || (val < 0))
  {
    return false;
  }
  *out_ms = (uint32_t)val;
  return true;
}

/**
  * @brief  Claim a free async slot: bump its id and arm the delay.
  * @param  slot slot to claim
  * @param  ms accepted delay
  * @retval false if the slot is still busy (state untouched)
  */
static bool rtt_async_claim(rtt_async_slot_t *slot, uint32_t ms)
{
  if (slot->pending)
  {
    return false;
  }
  slot->pending = true;
  slot->id++;
  slot->start = HAL_GetTick();
  slot->due = ms;
  return true;
}

/**
  * @brief  Cancel a pending async request or report there is none.
  * @retval None
  */
static void rtt_async_cancel(rtt_async_slot_t *slot, const char *name)
{
  if (slot->pending)
  {
    slot->pending = false;   /* id stays consumed: next accept is #N+1 */
    SEGGER_RTT_printf(0, "%s #%u cancelled\r\n", name, (unsigned)slot->id);
  }
  else
  {
    SEGGER_RTT_printf(0, "%s cancel: nothing pending\r\n", name);
  }
}

/**
  * @brief  Print one async channel's current state (idle or pending with
  *         remaining delay).
  * @retval None
  */
static void rtt_async_report(const rtt_async_slot_t *slot, const char *name)
{
  if (slot->pending)
  {
    uint32_t elapsed = HAL_GetTick() - slot->start;
    uint32_t left = (elapsed >= slot->due) ? 0u : (slot->due - elapsed);
    SEGGER_RTT_printf(0, "%s: #%u pending, %u ms left\r\n",
                      name, (unsigned)slot->id, (unsigned)left);
  }
  else
  {
    SEGGER_RTT_printf(0, "%s: idle\r\n", name);
  }
}

/**
  * @brief  async1 [ms]: acknowledge now, print the result ms later from the main loop.
  * @retval None
  */
static void cmd_async1(int argc, char *argv[])
{
  uint32_t ms = RTT_DLY_DEFAULT_MS;
  rtt_async_slot_t *slot = &s_async[RTT_ASYNC_SLOT_1];

  if ((argc == 2) && (strcmp(argv[1], "cancel") == 0))
  {
    rtt_async_cancel(slot, s_async_name[RTT_ASYNC_SLOT_1]);
    return;
  }
  if (argc > 2)
  {
    SEGGER_RTT_printf(0, "usage: %s\r\n", s_async1_usage);
    return;
  }
  if ((argc == 2) && !cmd_parse_ms(argv[1], &ms))
  {
    SEGGER_RTT_printf(0, "bad ms '%s'\r\n", argv[1]);
    return;
  }

  if (!rtt_async_claim(slot, ms))
  {
    SEGGER_RTT_printf(0, "async1 busy: #%u still pending\r\n", (unsigned)slot->id);
    return;
  }
  SEGGER_RTT_printf(0, "async1 #%u accepted, due in %u ms\r\n",
                    (unsigned)slot->id, (unsigned)ms);
}

/**
  * @brief  async2 [ms] [quiet]: silent accept; 'done' ms later is the only feedback.
  * @retval None
  */
static void cmd_async2(int argc, char *argv[])
{
  uint32_t ms = RTT_DLY_DEFAULT_MS;
  bool quiet = false;
  rtt_async_slot_t *slot = &s_async[RTT_ASYNC_SLOT_2];

  if ((argc == 2) && (strcmp(argv[1], "cancel") == 0))
  {
    rtt_async_cancel(slot, s_async_name[RTT_ASYNC_SLOT_2]);
    return;
  }

  /* Consume the optional 'quiet' token first, then fall through to the same
     numeric path as async1. 'async2 quiet 500' (reversed) is not a form. */
  if ((argc == 2) && (strcmp(argv[1], "quiet") == 0))
  {
    quiet = true;
    argc = 1;   /* 'async2 quiet': default delay, silent rejection */
  }
  else if (argc == 3)
  {
    if (strcmp(argv[2], "quiet") != 0)
    {
      SEGGER_RTT_printf(0, "usage: %s\r\n", s_async2_usage);
      return;
    }
    quiet = true;
    argc = 2;   /* 'async2 500 quiet': drop the quiet token */
  }
  if (argc > 2)
  {
    SEGGER_RTT_printf(0, "usage: %s\r\n", s_async2_usage);
    return;
  }
  if ((argc == 2) && !cmd_parse_ms(argv[1], &ms))
  {
    SEGGER_RTT_printf(0, "bad ms '%s'\r\n", argv[1]);
    return;
  }

  if (!rtt_async_claim(slot, ms))
  {
    /* quiet silences only the rejection, never the done print */
    if (!quiet)
    {
      SEGGER_RTT_printf(0, "async2 busy: #%u still pending\r\n", (unsigned)slot->id);
    }
    return;
  }
  /* accepted: silent by design, the done print is the only feedback */
}

/**
  * @brief  async: print both async channels' current state.
  * @retval None
  */
static void cmd_async_status(int argc, char *argv[])
{
  (void)argc;
  (void)argv;
  rtt_async_report(&s_async[RTT_ASYNC_SLOT_1], s_async_name[RTT_ASYNC_SLOT_1]);
  rtt_async_report(&s_async[RTT_ASYNC_SLOT_2], s_async_name[RTT_ASYNC_SLOT_2]);
}

/**
  * @brief  echo <text> [text...]: echo tokens joined by single spaces.
  * @retval None
  */
static void cmd_echo(int argc, char *argv[])
{
  int i;

  if (argc < 2)
  {
    SEGGER_RTT_printf(0, "usage: %s\r\n", s_echo_usage);
    return;
  }
  SEGGER_RTT_WriteString(0, "echo:");
  for (i = 1; i < argc; i++)
  {
    SEGGER_RTT_printf(0, " %s", argv[i]);
  }
  SEGGER_RTT_WriteString(0, "\r\n");
}

/* ---- periodic 1 s tick print (non-blocking scheduler) ---- */

/**
  * @brief  Print the uptime counter once per second, when enabled.
  * @retval None
  */
static void rtt_tick_task(void)
{
  uint32_t now = HAL_GetTick();

  if ((now - s_tick_ref) >= RTT_TICK_PERIOD_MS)
  {
    s_tick_ref += RTT_TICK_PERIOD_MS;   /* += keeps the cadence drift-free */
    s_seconds++;
    if (s_tick_enabled && (s_line_len == 0u))   /* never interrupt an in-progress input line */
    {
      SEGGER_RTT_printf(0, "\r\ntick=%u\r\n" RTT_PROMPT, (unsigned)s_seconds);
      log_hex("tick=", s_seconds, 8u);  /* UART mirror, stays in sync with RTT */
    }
  }
}

/**
  * @brief  Print the due async results, slot 1 then slot 2; hold while a line
  *         is in progress.
  * @retval None
  */
static void rtt_async_task(void)
{
  uint32_t i;

  for (i = 0u; i < RTT_ASYNC_SLOT_COUNT; i++)
  {
    rtt_async_slot_t *slot = &s_async[i];
    if (slot->pending && ((HAL_GetTick() - slot->start) >= slot->due))
    {
      /* Hold until no line is in progress: an unterminated host line keeps the
         result waiting (unlike tick, which skips) -- it is never dropped. */
      if (s_line_len == 0u)
      {
        slot->pending = false;
        SEGGER_RTT_printf(0, "\r\n%s #%u done\r\n" RTT_PROMPT,
                          s_async_name[i], (unsigned)slot->id);
      }
    }
  }
}
/* USER CODE END 4 */

 /* MPU Configuration */

void MPU_Config(void)
{

  /* Disables the MPU */
  HAL_MPU_Disable();

  /* Enables the MPU */
  HAL_MPU_Enable(MPU_PRIVILEGED_DEFAULT);

}

/**
  * @brief  Period elapsed callback in non blocking mode
  * @note   This function is called  when TIM6 interrupt took place, inside
  * HAL_TIM_IRQHandler(). It makes a direct call to HAL_IncTick() to increment
  * a global variable "uwTick" used as application time base.
  * @param  htim : TIM handle
  * @retval None
  */
void HAL_TIM_PeriodElapsedCallback(TIM_HandleTypeDef *htim)
{
  /* USER CODE BEGIN Callback 0 */

  /* USER CODE END Callback 0 */
  if (htim->Instance == TIM6)
  {
    HAL_IncTick();
  }
  /* USER CODE BEGIN Callback 1 */

  /* USER CODE END Callback 1 */
}

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */
