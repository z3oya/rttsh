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

- **Lua 自动化在板验证（script）**：`rtt.*` API 覆盖发送、期望、二进制收发与毫秒级计时断言；expect 超时即失败，错误信息带接收缓冲区尾部。
- **零额外通信外设**：RTT 经调试探针收发，固件不占 UART；会话按"探针 + 通道"互斥。
- **交互监控（monitor）**：直接运行即进入实时终端；`-tui` 为对话式布局，支持历史翻阅、光标移动编辑，历史跨会话持久化。
- **单发探测（send）**：发送一行并接收应答（`--wait`），支持十六进制原始字节。
- **控制块地址解析（`--elf`）**：从固件镜像的 `_SEGGER_RTT` 符号解析 RTT 控制块地址，随重编译自动更新。
- **JSON 配置文件**：`.rttsh/config.json` 按工作目录自动加载芯片名等默认值，优先级为命令行 > 配置文件 > 内置默认值。
- **设备数据库查询（list-devices）**：确认 `--chip` 在 J-Link 设备库中的准确拼写。
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
rttsh send "6c 65 64 20 67 20 6f 6e 0a" --hex --chip STM32H743XI --wait 300   # 原始字节 "led g on\n"
```

载荷必须紧跟子命令，选项放在载荷之后；发送 `--` 开头的内容用 `send -- --value`，详见 `rttsh send --help`。

### 3. 交互监控

```bash
rttsh --chip STM32H743XI          # 默认子命令为 monitor
rttsh --chip STM32H743XI -tui     # 对话式布局，↑/↓ 翻历史、←/→/Home/End 移动光标
```

键盘输入按行发送（行尾由 `--eol` 决定，默认 `lf`），Ctrl+C 退出；输出重定向时转为只收模式，`--wait 1500` 接收 1.5 秒后退出，可接 `grep` 等过滤。

### 4. Lua 脚本

```bash
rttsh script --eval 'rtt.send("led r on"); rtt.expect("LED r on", 500)' --chip STM32H743XI
```

```lua
-- smoke.lua
rtt.send("led r toggle")
rtt.expect("LED r toggled", 500)
rtt.log("smoke PASS")
```

expect 失败时退出码为 1，错误信息包含缓冲区尾部，可直接看到固件的实际应答：

```
rttsh: smoke.lua:2: expect: 'LED r = ON' not found within 500 ms; buffer tail: "led r toggle\r\nLED r toggled\r\nrtt> "
```

### 5. 配置文件

`examples/stm32/.rttsh/config.json`：

```json
{ "chip": "STM32H743XI", "scriptTimeout": 20000 }
```

在该目录下运行时自动加载，无需再指定 `--chip`：

```bash
cd examples/stm32
rttsh script Tests/t01_async1_accept_done.lua    # t01 PASS (id=43)
```

`-C/--root <dir>` 以 git -C 语义切换运行基准目录：隐式配置、`.rttsh/` 状态与所有相对路径均锚定到该目录。

### 6. 用 --elf 免去手工地址

```bash
rttsh send "help" --elf MDK-ARM/stm32-project/stm32-project.axf --wait 100
# rttsh: --elf: _SEGGER_RTT at 0x24000070 (from '...')
```

从镜像的 `_SEGGER_RTT` 符号解析控制块地址，monitor / send / script 均支持；解析结果缓存在 `.rttsh/elf-cache/`，显式 `--rtt-addr` 优先于 `--elf`。

## 命令

| 命令 | 作用 |
|------|------|
| （无子命令） | monitor：交互式 RTT 终端 |
| `send <text>` | 发送一次，可选等待应答 |
| `script [<file.lua>]` | 运行 Lua 自动化脚本（或 `--eval <lua 代码>`） |
| `list-devices` | 列出 J-Link DLL 设备数据库（可 `--filter` 过滤） |
| `manual` | 打印参考手册 |

退出码：**0** 成功，**1** 运行失败（含 expect 超时），**2** 用法错误。exit 0 只表示 rttsh 自身无错误：固件层的错误（命令不存在、参数非法）同样 exit 0，判读必须依据应答文本。

## 文档

- `rttsh manual` — 完整参考：配置文件全部键与类型规则、`--elf` 解析规则、`rtt.*` API、脚本编写要点（捕获、否定断言、计时断言、Lua 模式）与运行机制。
- `rttsh --help` / `rttsh <命令> --help` — 全部选项、默认值与退出码。
- `examples/stm32` — 示例固件与在板测试套件；`skills/rttsh-verify-on-board` — AI 编码代理技能。
