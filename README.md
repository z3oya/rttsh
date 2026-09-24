# rttsh

**rttsh** 是一个可脚本化的 SEGGER J-Link RTT 命令行终端，用于固件的自动化在板验证。固件侧链接 SEGGER RTT 并内置一个命令行控制台，主机侧用 Lua 脚本发送命令、匹配应答、断言输出与时序，使上板验证从人工终端操作转变为可批量执行、可纳入 CI 的自动化测试。

仓库同时提供面向 AI 编码代理（Claude Code / Codex 等）的技能 `skills/rttsh-verify-on-board`。

```
rttsh send "led r on" --chip STM32H743XI --wait 300
led r on
LED r on
rttsh script Tests/t01_async1_accept_done.lua
t01 PASS (id=43)
```

## 功能特性

- **Lua 自动化在板验证（script）**：`rtt.*` API 覆盖发送、期望、二进制收发与毫秒级计时断言；`--eval` 可内联执行；expect 超时即失败（exit 1），失败信息包含接收缓冲区尾部，便于定位。
- **零额外通信外设**：RTT 经由调试探针收发，固件不占 UART，主机不接串口线；rttsh 会话按"探针 + 通道"互斥，报错信息中包含占用者 PID。
- **交互监控（monitor）**：不带子命令直接运行即进入实时终端；`-tui` 为对话式布局（上方日志、底部固定输入行，↑/↓ 翻阅历史命令，历史跨会话保存于 `.rttsh/tui-history.json`）；输出重定向时转为接收 N 毫秒后退出（默认 500 ms），可接管道过滤。
- **单发探测（send）**：发送一行命令并接收应答（`--wait`）；支持十六进制原始字节（`--hex`）与 `\n \r \t \\` 转义。
- **控制块地址解析（`--elf`）**：从固件镜像的 `_SEGGER_RTT` 符号（ELF32）解析 RTT 控制块地址，地址随每次重编译自动更新，无需从 .map 文件手工复制。
- **JSON 配置文件**：`.rttsh/config.json` 按工作目录自动加载（与 `--elf` 解析缓存、`-tui` 输入历史同在 `.rttsh/` 目录下），保存芯片名、超时等默认值；优先级为命令行 > 配置文件 > 内置默认值。
- **设备数据库查询（list-devices）**：列出 J-Link DLL 支持的芯片名称，可 `--filter` 过滤，用于确认 `--chip` 的准确拼写。
- **内置参考手册**：`rttsh manual` 打印配置文件键、`rtt.*` API 与脚本编写指南，不依赖网络文档。

## 环境要求

| 依赖 | 说明 |
|------|------|
| Windows | 依赖 JLink_x64.dll 与 lua54 原生库，当前面向 Windows 发布 |
| .NET 10 运行时 | framework 安装包需要；self-contained 包自带 |
| SEGGER J-Link 驱动 + 探针 | 需要 J-Link 软件（含 JLink DLL，rttsh 自动探测，也可 `--dll` 指定） |
| 目标固件 | 已链接 SEGGER RTT（含 `_SEGGER_RTT` 控制块）并烧录到目标板 |


## 快速上手

### 1. 确认芯片名称

`--chip` 必须与 J-Link 设备数据库中的名称完全一致：

```bash
rttsh list-devices --filter H743
```

### 2. 单发命令，接收应答

```bash
rttsh send "led r on" --chip STM32H743XI --wait 300
# led r on            固件回显
# LED r on            应答行
```

`send` 的载荷必须紧跟子命令，选项放在载荷之后（`send --hex "..."` 报 `missing <text> payload` 退出码 2，`send "..." --hex` 正确）：

```bash
rttsh send "6c 65 64 20 67 20 6f 6e 0a" --hex --chip STM32H743XI --wait 300   # 原始字节 "led g on\n"
rttsh send "led r" --chip STM32H743XI --wait 300                              # 两 token 形式为状态查询
```

发送以 `--` 开头的内容：`send -- --value`（`--` 之后不能再带其它选项），或改用十六进制载荷 `send "2d 2d ..." --hex`。

### 3. 交互监控

```bash
rttsh --chip STM32H743XI          # 默认子命令为 monitor
rttsh --chip STM32H743XI -tui     # 对话式布局与输入历史
```

monitor 中键盘输入按行发送（行尾由 `--eol` 决定，默认 `lf`），Ctrl+C 退出。输出重定向（管道/文件）时转为只收模式，`--wait 1500` 接收 1.5 秒后退出，可接 `grep` 等过滤。

`-tui` 会话的 ↑/↓ 历史跨会话持久化：保存在工作目录的 `.rttsh/tui-history.json`（最新 500 条，会话退出时重写；删除该文件总是安全的）。

### 4. Lua 脚本

内联形式：

```bash
rttsh script --eval 'rtt.send("led r on"); rtt.expect("LED r on", 500)' --chip STM32H743XI
```

脚本文件形式（`examples/stm32` 固件的应答为 `LED r toggled`）：

```lua
-- smoke.lua
rtt.send("led r toggle")
rtt.expect("LED r toggled", 500)
rtt.log("smoke PASS")
```

```bash
rttsh script smoke.lua --chip STM32H743XI
# smoke PASS
```

expect 失败时退出码为 1，错误信息包含缓冲区尾部，可直接看到固件的实际应答：

```
rttsh: smoke.lua:2: expect: 'LED r = ON' not found within 500 ms; buffer tail: "led r toggle\r\nLED r toggled\r\nrtt> "
```

### 5. 使用配置文件保存连接参数

`examples/stm32/.rttsh/config.json`：

```json
{ "chip": "STM32H743XI", "scriptTimeout": 20000 }
```

在该目录下运行时自动加载（按工作目录查找），无需再指定 `--chip`：

```bash
cd examples/stm32
rttsh script Tests/t01_async1_accept_done.lua    # t01 PASS (id=43)
```

### 6. 参考手册

```bash
rttsh manual     # 配置文件键、rtt.* API、脚本编写指南
rttsh --help     # 全部选项与默认值
```

## 命令与选项

### 子命令

| 命令 | 作用 |
|------|------|
| （无子命令） | monitor：交互式 RTT 终端 |
| `send <text>` | 发送一次，可选等待应答 |
| `script [<file.lua>]` | 运行 Lua 自动化脚本（或 `--eval <lua 代码>`） |
| `list-devices` | 列出 J-Link DLL 设备数据库（可 `--filter` 过滤） |
| `manual` | 打印参考手册 |

退出码约定：**0** 成功，**1** 运行失败（含 expect 超时），**2** 用法错误。exit 0 只表示 rttsh 自身无错误：固件层的错误（命令不存在、参数非法）同样 exit 0，判读必须依据应答文本（见"实测判读规则"）。

### 常用选项

| 选项 | 默认 | 说明 |
|------|------|------|
| `--chip <name>` | — | 目标设备名（monitor / send 必填），如 `STM32H743XI` |
| `--elf <path>` | — | 从固件镜像的 `_SEGGER_RTT` 符号解析控制块地址（ELF32） |
| `--rtt-addr <hex>` | SDK 自动扫描 | 已知控制块地址；显式给出时优先于 `--elf` |
| `--rtt-range <hex>` | — | 搜索 `"SEGGER RTT"` 签名的字节范围 |
| `--speed <kHz>` | 4000 | 接口速率 |
| `--if <swd\|jtag>` | swd | 目标接口 |
| `--channel <0-15>` | 0 | RTT 上/下行通道对 |
| `--sn <number>` | 第一个探针 | 探针 USB 序列号 |
| `--dll <path>` | 自动探测 | JLink DLL 路径 |
| `--reset` / `--no-reset` | 不复位 | 连接时是否复位目标（默认不复位） |
| `--eol <lf\|cr\|crlf\|none>` | lf | 发送文本时追加的行尾；`--hex` 载荷为原始字节、不追加 |
| `--encoding <utf8\|ascii\|latin1>` | utf8 | 接收字节解码方式 |
| `--hex` | — | 接收显示为每行 16 字节的十六进制转储；send 时把载荷按十六进制解析发送 |
| `-tui` | 关 | monitor 专用对话式布局 |
| `--log <file>` | — | 将收到的原始字节同时追加到文件 |
| `--wait <ms>` | send 500 / 交互无限制 | send / 重定向 monitor 打印接收数据的时长 |
| `--script-timeout <ms>` | 关（0） | 整个脚本的硬性时限，在 `rtt.*` 调用边界检查 |
| `--verbose` | 关 | 显示 J-Link 连接过程日志 |
| `-c, --config <path>` | `./.rttsh/config.json` | 从 JSON 文件加载选项默认值 |
| `-C, --root <dir>` | — | 以 `<dir>` 为运行基准目录（git -C 语义）：隐式配置、`.rttsh/` 状态与所有相对路径均锚定到该目录 |

## 配置文件

rttsh 支持从 JSON 文件读取选项默认值：显式 `--config <path>`（文件必须存在，否则退出码 2），或自动拾取工作目录下的 `./.rttsh/config.json`（存在则读取，缺失时跳过）。

`-C/--root <dir>` 整体切换运行基准目录：隐式配置改从 `<dir>/.rttsh/config.json` 读取，`--elf` 解析缓存落在 `<dir>/.rttsh/elf-cache/`，所有相对路径（`elf`、`log`、`script`、`dll` 及显式 `--config`）也相对 `<dir>` 解析。`<dir>` 必须存在；其中没有配置不算错误。显式 `-c` 文件优先于根目录下的默认配置。

优先级（逐选项）：**命令行 > 配置文件 > 内置默认值**；JSON `null` 视为未设置；同名键重复时后者生效。

```json
{
  "chip": "STM32H743XI",
  "speed": 4000,
  "interface": "swd",
  "rttAddr": "0x20000000",
  "rttRange": "0x1000",
  "sn": 0,
  "channel": 0,
  "encoding": "utf8",
  "eol": "lf",
  "log": "capture.bin",
  "wait": 500,
  "scriptTimeout": 20000
}
```

全部 14 个键均为可选，与 CLI 选项一一对应。键名为 camelCase 而非 CLI 拼写（`--if` 对应 `interface`、`--script-timeout` 对应 `scriptTimeout`；按 CLI 拼写写入报 `unknown key`，退出码 2）：

| 键 | 说明 |
|----|------|
| `chip` | 设备名，须与 list-devices 输出完全一致 |
| `speed` | 接口速率 kHz（默认 4000） |
| `interface` | `"swd"` 或 `"jtag"` |
| `rttAddr` | 控制块地址，十六进制字符串 `"0x..."`（写成数字报类型错误，裸 `0x...` 不是合法 JSON） |
| `rttRange` | 扫描范围（字节），十六进制字符串 |
| `elf` | 固件镜像路径（ELF32）；显式 `rttAddr` 优先；`""` 表示未设置 |
| `sn` | 探针 USB 序列号 |
| `channel` | RTT 上/下行通道对 0-15（默认 0） |
| `dll` | JLink DLL 路径（`""` 表示未设置） |
| `encoding` | `"utf8"` / `"ascii"` / `"latin1"` |
| `eol` | `"lf"` / `"cr"` / `"crlf"` / `"none"` |
| `log` | 原始接收字节追加写入的文件 |
| `wait` | send / 重定向 monitor 打印时长（ms） |
| `scriptTimeout` | 脚本整体时限（ms），0 = 关闭 |

类型错误、未知或拼写错误的键、非法 JSON 均以 `--config: ...` 报错并退出码 2。`hex`、`tui`、`reset`、`filter`、`config` 五个名字即使是 `null` 也会被拒绝——它们选择模式或指代文件本身，不属于可保存的默认值。配置文件只被 monitor、send、script、list-devices 读取；`--help`、`--version`、`manual` 不读取，配置文件错误不影响这些命令的可用性。

## 用 --elf 解析控制块地址

`--elf <镜像>` 从固件的 `_SEGGER_RTT` 符号（ELF32）解析控制块地址，地址随重编译自动更新：

```bash
rttsh send "help" --elf MDK-ARM/stm32-project/stm32-project.axf --wait 100
# rttsh: --elf: _SEGGER_RTT at 0x24000070 (from 'MDK-ARM/stm32-project/stm32-project.axf')
```

确认信息打印到 stderr，stdout 保持可管道化。monitor / send / script 三个连接命令均支持，也可写入配置文件作为每个会话的默认值。

规则：

- 显式 `--rtt-addr` / `rttAddr` 优先，此时 `--elf` 不被读取——临时调试地址不受过期镜像影响；经 `--elf` 解析的地址按原值使用，`--rtt-range` 被忽略并给出警告（避免窗口扫描匹配到另一个 `"SEGGER RTT"` 签名）。
- 该功能消除的故障模式：手工固定的错误地址使 RTT 打开在错误位置，写入持续无进展——`down-buffer made no progress ... (wrote 0/5 bytes)`。
- 镜像中无该符号（固件未集成 RTT 或已 strip）仅警告，并回退到 SDK 的 RAM 扫描。
- 文件级问题（路径不存在、不是 ELF、ELF64）为用法错误，退出码 2。
- 解析结果缓存于 `./.rttsh/elf-cache/`：按镜像大小 + 修改时间比对，命中前再用条目内的内容哈希（SHA256）校验，因此时间戳相同的内容调包也不会用到过期符号；未重建的镜像可跳过解析。镜像原始字节不入缓存，删除 `.rttsh/elf-cache/` 总是安全的（旁边的 `.rttsh/config.json` 是配置文件）。

## Lua 脚本

### rtt.* API

| 函数 | 说明 |
|------|------|
| `rtt.send(text)` | 发送文本；追加 `--eol` 行尾 |
| `rtt.send_hex("DE AD")` | 发送按十六进制文本解析的原始字节（逐字节精确，不追加行尾） |
| `rtt.log(line)` | 打印到 stderr，如进度信息 |
| `rtt.wait(ms)` | 返回 ms 毫秒内**新到**的文本，无数据返回 `""`（见下方判读规则） |
| `rtt.wait_hex(ms)` | 同一接收窗口，以十六进制文本返回（逐字节精确） |
| `rtt.expect(pattern, ms)` | 等待 Lua 模式匹配并返回截至匹配结束的文本（消费式）；ms 默认 1000；超时抛错 |
| `rtt.now()` | 脚本启动以来的单调毫秒数 |
| `rtt.sleep(ms)` | 暂停脚本 |
| `rtt.exit(code)` | 停止脚本并以 code 退出（默认 0） |

收发文本均遵循 `--encoding`。`--eol none` 时不追加行尾，需自行嵌入 `\n`，否则连续发送的内容合并为一行。

### 编写要点

**基本模式是 send/expect 配对**：发送一条命令，expect 应答中的一个稳定子串。超时抛错并终止脚本（退出码 1），无应答时立即失败而非无限等待：

```lua
rtt.send("led r on")
rtt.expect("LED r on", 500)
```

**捕获变化的值**（id、计数器）用 `string.match` 提取后复用，不硬编码，使脚本在目标重启、编号变化后无需修改即可复用：

```lua
local r  = rtt.expect("async1 #%d+ accepted", 500)
local id = r:match("#(%d+)")        -- 只捕获数字
rtt.expect("async1 #"..id.." done", 2000)
```

捕获组只圈数字：`"#(%d+)"` 得到 `"18"`，`"(#%d+)"` 得到 `"#18"`，而 `tonumber("#18")` 为 nil。

**断言"不得出现"**：将 expect 放入 pcall 并要求其超时，随后探测设备，确认静默窗口后控制台仍然可用：

```lua
local printed = pcall(function() rtt.expect("busy", 400) end)
assert(not printed, "quiet path printed a busy log")
rtt.send("tick")
rtt.expect("tick: off", 500)
```

**计时断言**使用 `rtt.now()`（示例固件实测：请求 100/300/700 ms，端到端 140/328/735 ms）：

```lua
local t0 = rtt.now()
rtt.send("async1 300")
rtt.expect("async1 #%d+ accepted, due in 300 ms", 500)
rtt.expect("async1 #%d+ done", 2000)
local elapsed = rtt.now() - t0
assert(elapsed >= 280 and elapsed <= 1300, "timing out of window")
```

**Lua 5.4 模式，不是正则**：

- 裸 `[` 开启字符类，不完整的类立即抛错；匹配字面方括号用 `%[`（`]` 用 `%]`）。
- `%%` 匹配一个 `%`；`%d+` 表示一个或多个数字。
- `^` 和 `$` 锚定整个累积接收缓冲，不是单行——不能用于测试行尾。

**严格配对 send 与 expect**：expect 为推进式匹配，从上次成功匹配的终点继续扫描，已消费文本不可再次匹配；每个 expect 消费缓冲区直至匹配结束；新脚本从空缓冲开始。

### 运行机制

- 任何 `rtt.*` 失败都是可被 pcall 捕获的 Lua 错误并携带原因；未捕获的错误以退出码 1 终止脚本并打印消息（expect 失败信息包含缓冲区尾部）。
- 退出码：0 成功，1 失败，2 用法错误。
- `--script-timeout <ms>` 终止无响应的脚本（0/缺省 = 关闭），在 `rtt.*` 调用边界强制执行。
- 第一次 Ctrl+C 请求脚本在下一个 `rtt.*` 边界停止；第二次 Ctrl+C 立即退出。
- script 模式 stdout 不回显固件输出，判读在 Lua 内完成；`rtt.log()` 输出到 stderr。

