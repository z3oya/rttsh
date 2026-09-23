namespace Toolbox.Tools.RttCli;

/// <summary>Plain-text reference printed by `rtt-cli manual`: the config file,
/// the rtt.* scripting API, and how to write scripts with them.</summary>
internal static class Manual
{
    public const string Text = """
        rtt-cli manual

        The full option list with defaults is in rtt-cli --help; subcommands keep
        their own page (rtt-cli send --help, rtt-cli script --help, ...).

        1. CONFIGURATION FILE (--config / -c)

        rtt-cli takes option defaults from a JSON file. Name one with --config
        <path>, or let rtt-cli pick up ./.rttsh.config.json from the working
        directory when present. An explicit path must exist (a missing file
        exits 2); the implicit default is optional.

        Precedence, per option: command line > config file > built-in default.
        A JSON null counts as unset (the command line or the default wins);
        when a key repeats, the last occurrence wins.

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

        All keys are optional; the table below lists all fourteen. They
        mirror the CLI options in camelCase - "interface" is what --help
        spells --if:

          chip           device name exactly as list-devices prints it
          speed          interface speed in kHz (default 4000)
          interface      "swd" | "jtag"
          rttAddr        control-block address, hex string "0x..."
          rttRange       scan range in bytes, hex string "0x..."
          elf            firmware image (ELF32) for control-block lookup
                         via --elf; explicit rttAddr wins; "" = unset
          sn             probe USB serial number
          channel        RTT up/down channel pair, 0-15 (default 0)
          dll            JLink DLL path (default: auto-detect; "" = unset)
          encoding       "utf8" | "ascii" | "latin1"
          eol            "lf" | "cr" | "crlf" | "none"
          log            file to append raw received bytes to
          wait           ms to keep printing after the payload
                         (send / redirected monitor)
          scriptTimeout  hard limit for a whole script in ms, 0 = off

        Addresses are strings, not numbers: "rttAddr": "0x20000000" loads,
        while "rttAddr": 20000000 fails the type check, and a bare
        0x20000000 is not even valid JSON. Wrong types, unknown or
        misspelled keys, and unparsable JSON all exit 2 with a
        "--config: ..." message. Five names are rejected even when null -
        hex, tui, reset, filter, config - because they select a mode or
        name the file itself, so they never belong in saved defaults.

        The file is read by monitor, send, script and list-devices only;
        --help, --version and this manual never touch it, so a broken config
        cannot take the reference down.

        2. --ELF: CONTROL-BLOCK LOOKUP FROM A FIRMWARE IMAGE

        --elf <image> resolves the RTT control-block address from the
        image's _SEGGER_RTT symbol (ELF32), so the address follows every
        rebuild instead of a hand-copied .map value:

          rtt-cli send "version" --chip STM32H743XI --elf app.axf --wait 500
          rtt-cli: --elf: _SEGGER_RTT at 0x24000070 (from 'app.axf')

        The stderr line confirms the pin; stdout stays pipeable. All three
        connection commands take it, and so does the config file, where it
        becomes the default for every session:

          rtt-cli script smoke.lua --chip STM32H743XI --elf build/app.axf

          { "chip": "STM32H743XI", "elf": "build/app.axf" }

        Rules worth remembering:

          - an explicit --rtt-addr/--rttAddr wins, and --elf is not even
            read - a temporary debug address must not be blocked by a
            stale image:
              rtt-cli send ping --elf app.axf --rtt-addr 0x20000000
              rtt-cli: --elf ignored: --rtt-addr/--rttAddr already pins
              the control block (0x20000000)
          - the failure --elf removes: a wrong hand-pinned address opens
            RTT in the wrong place and writes starve -
            "down-buffer made no progress ... (wrote 0/5 bytes)". A
            resolved address is used as-is; any --rtt-range is ignored
            with a warning, since a window scan could lock onto a
            different "SEGGER RTT" hit
          - an image without the symbol (built without RTT, or stripped)
            only warns and falls back to the SDK RAM scan:
              --elf: _SEGGER_RTT not in 'app.elf' (built without RTT,
              or stripped); falling back to the SDK RAM scan
          - file-level problems are usage errors (exit 2): a missing
            path, a file that is not an ELF image, an ELF64 image

        3. SCRIPTING: THE rtt.* API

          rtt.send(text)             send text; appends the --eol terminator
          rtt.send_hex("DE AD")      send raw bytes parsed from hex text (byte-exact)
          rtt.log(line)              print to stderr, e.g. progress notes
          rtt.wait(ms)               return what arrives within ms as text
                                     (ASCII-reliable), or "" when nothing does
          rtt.wait_hex(ms)           same receive window as hex text (byte-exact)
          rtt.expect(pattern, ms)    wait until the Lua pattern matches and return
                                     the text through the match end (consuming);
                                     ms defaults to 1000; a timeout raises an
                                     error. Full Lua 5.4 pattern syntax and
                                     scans forward from the end of the last match -
                                     consumed text is never re-matched.
          rtt.now()                  monotonic milliseconds since the script started
          rtt.sleep(ms)              pause the script
          rtt.exit(code)             stop the script and exit with code (default 0)

        Text in and out follows --encoding; rtt.send appends the --eol
        terminator. With --eol none nothing is appended - embed \n yourself
        or consecutive sends run together on one line.

        4. WRITING SCRIPTS

        The workhorse is a send/expect pair - send a command, then expect a
        stable substring of the reply. A timeout raises and stops the script
        (exit 1), so a missing reply fails loudly instead of hanging:

          rtt.send("led r on")
          rtt.expect("LED r on", 500)

        rtt.expect returns everything up to the match end. Capture changing
        values (ids, counters) with string.match and reuse them instead of
        hard-coding, so a script survives reboots and renumbering:

          local r  = rtt.expect("async1 #%d+ accepted", 500)
          local id = r:match("#(%d+)")       -- capture the digits only
          rtt.expect("async1 #"..id.." done", 2000)

        Keep the capture on the digits: "#(%d+)" yields "18", while
        "(#%d+)" yields "#18" and tonumber("#18") is nil.

        To assert that something must NOT appear, expect it inside pcall and
        require the timeout; probe the device afterwards to prove the console
        stayed alive through the silent window:

          local printed = pcall(function() rtt.expect("busy", 400) end)
          assert(not printed, "quiet path printed a busy log")
          rtt.send("tick")
          rtt.expect("tick: off", 500)

        Lua 5.4 patterns, not regex:
          - a bare [ opens a character class and an incomplete class raises
            immediately; match a literal bracket as %[ (and ] as %])
          - %% matches one %, %d+ is "one or more digits"
          - ^ and $ anchor the whole accumulated receive buffer, not single
            lines - never use them to test line endings

        Pair send and expect strictly: each expect consumes the buffer up to
        its match end, so a later pattern can no longer see that text; a
        fresh script starts with an empty buffer.

        A complete smoke script:

          -- smoke.lua    run: rtt-cli script smoke.lua --chip STM32H743XI
          rtt.send("led r toggle")
          rtt.expect("LED r = ON", 500)
          rtt.log("smoke PASS")

        or inline, without a file:

          rtt-cli script --eval 'rtt.send("ping"); rtt.expect("pong", 500)'

        5. MECHANICS

          - any rtt.* failure is a catchable Lua error carrying the reason
            (pcall); an uncaught error stops the script with exit code 1
            and prints the message
          - exit codes: 0 ok, 1 failure, 2 usage
          - --script-timeout <ms> aborts a stuck script (0 = off, the
            default); it is enforced at rtt.* call boundaries
          - the first Ctrl+C asks the script to stop at the next rtt.*
            boundary; a second Ctrl+C hard-exits
        """;
}
