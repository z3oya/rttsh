namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Plain-text rtt.* reference printed by `rtt-cli script --manual`.</summary>
internal static class LuaManual
{
    public const string Text = """
        rtt-cli Lua automation - the rtt.* API

          rtt.send(text)             send text; appends the --eol terminator
          rtt.send_hex("DE AD")      send raw bytes parsed from hex text (byte-exact)
          rtt.log(line)              print to stderr, e.g. progress notes
          rtt.wait(ms)               return what arrives within ms as text
                                     (ASCII-reliable), or "" when nothing does
          rtt.wait_hex(ms)           same receive window as hex text (byte-exact)
          rtt.expect(pattern, ms)    wait until the Lua pattern matches and return
                                     the text through the match end (consuming);
                                     ms defaults to 1000; a timeout raises an error
          rtt.now()                  monotonic milliseconds since the script started
          rtt.sleep(ms)              pause the script
          rtt.exit(code)             stop the script and exit with code (default 0)

        Text in and out follows --encoding; rtt.send appends the --eol terminator.

        A failing rtt.* call raises a catchable Lua error (use pcall) carrying the
        reason; an uncaught error stops the script with exit code 1. Exit codes:
        0 ok, 1 failure, 2 usage. --script-timeout <ms> aborts a stuck script
        (0 = off, the default). The first Ctrl+C asks the script to stop at the
        next rtt.* boundary; a second Ctrl+C hard-exits.

        Example
          rtt-cli script --eval 'rtt.send("led r on"); rtt.expect("LED r on", 500)'
        """;
}
