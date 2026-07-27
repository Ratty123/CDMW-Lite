using System.Globalization;
using System.IO.Pipes;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Worker;

internal static class WorkerProgram
{
    /// <summary>
    /// A worker that nobody claims has no client to serve and no window to close, so it waits only
    /// for a bounded period before exiting itself. The client owns a kill-on-close job object, but
    /// that fence is armed just after launch; this timeout covers the launch itself failing.
    /// </summary>
    private const string ConnectTimeoutEnvironmentVariable = "CDMW_ARCHIVE_LITE_WORKER_CONNECT_TIMEOUT_SECONDS";
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    private const int UnclaimedExitCode = 4;

    public static async Task<int> RunAsync(string[] args)
    {
        string? pipeName = null;
        var selfTest = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Length:
                    pipeName = args[++index];
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
            }
        }

        if (selfTest)
        {
            try
            {
                var native = new NativeArchiveCore();
                native.EnsureCompatible();
                Console.WriteLine($"CDMW Archive Lite worker self-test: OK (archive ABI {native.AbiVersion})");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"CDMW Archive Lite worker self-test: FAIL: {exception.Message}");
                return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("usage: CdmwArchiveLite.Worker --pipe <name> | --self-test");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            var connectTimeout = ResolveConnectTimeout();
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
            {
                connect.CancelAfter(connectTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                {
                    Console.Error.WriteLine(
                        $"No Archive Lite client connected within {connectTimeout.TotalSeconds:N0} seconds; " +
                        "the worker is exiting so it cannot outlive its client.");
                    return UnclaimedExitCode;
                }
            }

            var server = new WorkerServer(pipe);
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Worker transport failed: {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static TimeSpan ResolveConnectTimeout()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectTimeoutEnvironmentVariable);
        if (double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && seconds <= 600)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultConnectTimeout;
    }
}
