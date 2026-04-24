using System.Diagnostics;

namespace YobaConf.Runner;

// Real child-exec: starts the child command with the fetched env applied and transparently
// forwards stdin/stdout/stderr. Returns the child's exit code; on cancellation we send a
// polite termination request and wait briefly before giving up.
public sealed class ProcessChildExec : IChildExec
{
    public async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> env,
        IReadOnlyList<string> childArgs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(childArgs);
        if (childArgs.Count == 0)
            throw new ArgumentException("childArgs must contain at least the command", nameof(childArgs));

        var psi = new ProcessStartInfo
        {
            FileName = childArgs[0],
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        for (var i = 1; i < childArgs.Count; i++)
            psi.ArgumentList.Add(childArgs[i]);

        foreach (var (k, v) in env)
            psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start child process.");

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // SIGINT / SIGTERM came in while we waited — signal the child and give it 2
            // seconds to exit cleanly before returning.
            try { process.CloseMainWindow(); } catch { /* not applicable on non-GUI */ }
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* gave up */ }
        }

        return process.HasExited ? process.ExitCode : 137;  // 137 = SIGKILL convention
    }
}
