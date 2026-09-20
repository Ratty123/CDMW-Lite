using System.Diagnostics;
using System.Text.Json;

namespace Cdmw.ArchiveLite.Core;

/// <summary>Serial, cancellable owner of the hidden Finder renderer. No archive access occurs here.</summary>
internal sealed class CharacterThumbnailRenderer : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private Process? _process;
    private bool _disposed;

    public CharacterThumbnailRenderer() => _idle = new Timer(_ =>
    {
        if (!_gate.Wait(0)) return;
        try { Stop(); } finally { _gate.Release(); }
    });

    public async Task RenderAsync(string executable, string key, string package, string output, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            if (_process is null || _process.HasExited)
            {
                Stop();
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.ArgumentList.Add("--character-thumbnail-worker");
                _process = Process.Start(start) ?? throw new IOException("Could not start the character thumbnail renderer.");
                // Drain diagnostics without retaining an unbounded log or blocking the child's pipe.
                _process.ErrorDataReceived += static (_, _) => { };
                _process.BeginErrorReadLine();
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Key = key, Package = package, Output = output }).AsMemory(), deadline.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line = await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null || line.Length > 64 * 1024) throw new IOException("Character thumbnail renderer ended without a result.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.GetProperty("Key").GetString() != key)
                    throw new IOException("Character thumbnail renderer returned a stale result.");
                if (response.RootElement.GetProperty("Error").GetString() is { Length: > 0 } error)
                    throw new IOException(error);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Character thumbnail rendering timed out."); }
            _idle.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }

    private void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { } // The child may exit between HasExited and Kill.
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle.Dispose();
        Stop();
    }
}
