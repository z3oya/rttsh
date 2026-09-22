using System.Runtime.CompilerServices;
using System.Text;
using Toolbox.Core.Rtt;
using Toolbox.Core.SerialComm;
using Toolbox.Tools.RttCli.Scripting;

namespace Toolbox.Tools.RttCli;

/// <summary>The script command: runs a Lua script against a live RTT link. The script thread owns
/// the Lua state; transport events reach it only through ScriptRuntime (which subscribed itself).
/// Double Ctrl+C: the first press asks the script to stop at the next rtt.* boundary, the second
/// is left to default termination (a script stuck in pure Lua cannot be interrupted safely).
/// Script mode has no console-input reader, so CancelKeyPress is its primary, reliable cancel
/// path - unlike monitor, see MonitorSession's class doc.</summary>
internal static class ScriptSession
{
    public static int Run(ScriptCommand command)
    {
        CommandLineOptions options = command.Options;
        if (command.EvalSource is null && !File.Exists(command.ScriptPath))
            throw new UsageException($"script: file not found: {command.ScriptPath}");
        if (options.ScriptTimeoutMs is < 0)
            throw new UsageException("script: --script-timeout must be >= 0 ms (0 = off)");

        // --log is a root option, so script accepts it too. Opened here (with the other option
        // checks) so a bad path fails before the probe is touched, and wired to DataReceived
        // because script has no RttRenderer to do the capture.
        using var logStream = RttLogFile.Open(options.LogFile);

        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = SessionSupport.AcquireExclusive(config);
        if (guard is null) return 1;
        Encoding encoding = TextCodec.Resolve(options.EffectiveEncoding);
        byte[] eol = PayloadCodec.Terminator(options.Eol, options.EffectiveEncoding);

        using var transport = new JLinkRttTransport();
        if (logStream is not null)
            transport.DataReceived += data => RttLogFile.Append(logStream, data);
        var runtime = new ScriptRuntime(transport, encoding, eol, SessionSupport.WriteDiag, options.ScriptTimeoutMs ?? 0);

        if (!SessionSupport.OpenOrReport(transport, config)) return 1;

        bool cancelRequested = false;
        Console.CancelKeyPress += (_, e) =>
        {
            if (cancelRequested) return;   // second press: e.Cancel stays false, default kills us
            cancelRequested = true;
            e.Cancel = true;
            runtime.RequestCancel();
        };

        var exitCode = new StrongBox<int>(1);
        using var done = new ManualResetEventSlim(false);
        var scriptThread = new Thread(() =>
        {
            try
            {
                var host = command.EvalSource is not null
                    ? new LuaScriptHost(runtime, command.EvalSource, "=eval")
                    : new LuaScriptHost(runtime, File.ReadAllText(command.ScriptPath!), "@" + command.ScriptPath);
                exitCode.Value = host.Run();
            }
            catch (ScriptError ex)
            {
                SessionSupport.WriteDiag($"rtt-cli: {ex.Message}");
                exitCode.Value = 1;
            }
            catch (Exception ex)
            {
                // binding-internal or unexpected failure must not crash the process uncleanly
                SessionSupport.WriteDiag($"rtt-cli: script crashed: {ex.Message}");
                exitCode.Value = 1;
            }
            finally
            {
                done.Set();
            }
        })
        { IsBackground = true, Name = "rtt-script" };
        scriptThread.Start();

        done.Wait();
        transport.Close();   // joins the poll thread before we report
        scriptThread.Join();
        return exitCode.Value;
    }
}
