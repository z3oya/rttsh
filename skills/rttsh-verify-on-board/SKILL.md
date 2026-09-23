---
name: rttsh-verify-on-board
description: Automate on-target verification of a program's logic by driving its RTT console with rttsh - write Lua scripts that exercise commands and assert responses, output and timing on real hardware. Use when asked to verify target-side behavior on board, build board-level checks for firmware features, or automate hardware test runs.
---

# rttsh verify on board

rttsh is the tool; the thing under test is the target program's logic.
Scripts drive the program's console over J-Link RTT: send commands, expect
replies, assert behavior, output and timing - everything a hardware test
needs, automatable end to end.

Authoritative references - consult them first, do not rely on this file for
flags or scripting API details:

- `rttsh manual` - config file, the `rtt.*` scripting API (send/expect/log/now/...),
  script-writing guide
- `rttsh --help` - global options and defaults
- `rttsh script --help` - script mode options

