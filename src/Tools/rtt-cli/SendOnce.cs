using System.Runtime.CompilerServices;
using Toolbox.Core.Rtt;

namespace Toolbox.Tools.RttCli;

/// <summary>The send command: encode one payload, write it to the down channel, optionally wait
/// for a reply window, exit.</summary>
internal static class SendOnce
{
    public static int Run(SendCommand command)
    {
        CommandLineOptions options = command.Options;
        RttConnectionConfig config = options.ToConnectionConfig(resetDefault: false);
        using TargetLock? guard = SessionSupport.AcquireExclusive(config);
        if (guard is null) return 1;
        byte[] payload = PayloadCodec.Encode(command.Payload, options.EffectiveEncoding, options.Hex);
        // Line targets need a terminator to execute; --hex sends raw bytes untouched.
        if (!options.Hex)
            payload = [.. payload, .. PayloadCodec.Terminator(options.Eol, options.EffectiveEncoding)];

        using var transport = new JLinkRttTransport();
        using var renderer = new RttRenderer(options.EffectiveEncoding, options.Hex, options.LogFile, Console.Out, SessionSupport.ConsoleLock);
        using var done = new ManualResetEventSlim(false);
        var exitCode = new StrongBox<int>();

        SessionSupport.WireEvents(transport, renderer, done, exitCode, ui: null, options.Verbose);

        if (!SessionSupport.OpenOrReport(transport, config)) return 1;
        try
        {
            transport.Write(payload);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rtt-cli: {ex.Message}");
            return 1;
        }

        // Waiting is interruptible by link failure or Ctrl+C instead of a blind sleep.
        int waitMs = options.WaitMs ?? 0;
        if (waitMs > 0) done.Wait(waitMs);
        transport.Close();
        return exitCode.Value;
    }
}
