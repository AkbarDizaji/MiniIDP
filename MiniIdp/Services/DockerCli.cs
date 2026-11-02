using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace MiniIdp.Services;

public record DockerResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The most useful text for an error message: stderr if present, otherwise stdout.</summary>
    public string Diagnostics => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public class DockerCommandException(string message) : Exception(message);


public class DockerCli(IOptions<PlatformOptions> options, ILogger<DockerCli> logger)
{
    public virtual async Task<DockerResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null)
    {
        var startInfo = new ProcessStartInfo(options.Value.DockerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => Capture(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data);

        void Capture(StringBuilder buffer, string? line)
        {
            if (line is null) return; // null signals end of stream
            lock (buffer) buffer.AppendLine(line);
            onOutputLine?.Invoke(line);
        }

        logger.LogDebug("docker {Arguments}", string.Join(' ', arguments));
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new DockerCommandException(
                $"Could not start '{options.Value.DockerPath}'. Is Docker installed and on PATH? ({ex.Message})");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new DockerCommandException(
                $"'docker {arguments[0]}' timed out after {timeout.TotalSeconds:0} seconds.");
        }

        process.WaitForExit(); 
        return new DockerResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
