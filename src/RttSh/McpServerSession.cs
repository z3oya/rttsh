namespace Toolbox.Tools.RttCli;

/// <summary>The mcp command: runs the MCP server over stdio. The Rust rmcp layer owns the
/// protocol; this session owns the process console - the stdin pump feeds client bytes in,
/// the stdout pump drains server bytes out, and NOTHING else writes to stdout (diagnostics
/// go to stderr, like every command; the MCP JSON-RPC stream must own stdout alone). Chip
/// validation happens per connect tool call, not at startup; the target lock is taken by
/// connect and held until disconnect or shutdown. Exit path never calls Environment.Exit
/// (see Program's class doc): EOF or Ctrl+C sets done, the pumps wind down, and the native
/// session stops with its 2s grace bound for in-flight tool calls.</summary>
internal static class McpServerSession
{
    private const int ChunkBytes = 8192;
    private const int PollWaitMs = 100;

    public static int Run(McpCommand command)
    {
        using var host = new McpToolHost();
        using var session = McpNativeSession.Start(host.Dispatch);
        using var done = new ManualResetEventSlim(false);

        var stdin = new Thread(() =>
        {
            try
            {
                var buffer = new byte[ChunkBytes];
                Stream input = Console.OpenStandardInput();
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    session.Feed(buffer[..read]);
            }
            catch (Exception ex)
            {
                // Broad on purpose: a shutdown race (the session stopped under this pump)
                // throws like any pipe failure, and an exception escaping a background
                // thread would crash the whole process at exit. The client is gone or
                // going either way; the diagnostic lands on stderr.
                SessionSupport.WriteDiag($"rttsh-mcp: stdin pump stopped: {ex.Message}");
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true, Name = "mcp-stdin" };

        var stdout = new Thread(() =>
        {
            try
            {
                Stream output = Console.OpenStandardOutput();
                var buffer = new byte[ChunkBytes];

                void DrainToEmpty()
                {
                    int read;
                    while ((read = session.Drain(buffer)) > 0)
                        output.Write(buffer, 0, read);
                    output.Flush();
                }

                while (true)
                {
                    if (session.Wait(PollWaitMs) > 0)
                        DrainToEmpty();
                    if (done.IsSet)
                    {
                        // Final drain before teardown: a client that half-closed stdin right
                        // after its last request still gets the answer. Best effort - output
                        // the in-DLL server produces after this is dropped with the session.
                        DrainToEmpty();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Same shutdown-race reasoning as the stdin pump: broad catch, stderr
                // note, clean exit — never an unhandled exception at process teardown.
                SessionSupport.WriteDiag($"rttsh-mcp: stdout pump stopped: {ex.Message}");
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true, Name = "mcp-stdout" };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // drive a clean shutdown ourselves
            done.Set();
        };

        stdin.Start();
        stdout.Start();
        done.Wait();
        // The stdout pump does one final drain-to-empty after done; give it a moment so
        // the disposals below never race that last flush.
        stdout.Join(TimeSpan.FromSeconds(1));
        // using-disposals stop the native session first (2s grace for in-flight tool calls),
        // then the host closes the transport and releases the target lock.
        return 0;
    }
}
