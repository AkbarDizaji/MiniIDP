using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniIdp.Data;
using MiniIdp.Models;

namespace MiniIdp.Services;

/// <summary>
/// Everything that happens to a service: generate → build → run → health check, plus cleanup,
/// deletion, restart recovery and log retrieval. Registered as scoped because it uses the DbContext.
/// </summary>
public class ServiceProvisioner(
    PlatformDbContext db,
    DockerCli docker,
    IOptions<PlatformOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<ServiceProvisioner> logger)
{
    public const string NamePlaceholder = "__SERVICE_NAME__";
    public const int RuntimeLogLines = 200;

    // Every container and image we create carries these labels. Cleanup looks resources up by
    // the service-id label, so it never touches anything that belongs to someone else.
    private const string ManagedLabel = "mini-idp.managed=true";
    private const string ServiceIdLabel = "mini-idp.service-id";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly Lock LogFileLock = new();

    private PlatformOptions Options => options.Value;

    // ---------------------------------------------------------------- provisioning

    public async Task ProvisionAsync(ServiceRecord service, CancellationToken ct)
    {
        AppendLog(service, $"Provisioning '{service.Name}' (id {service.Id})");
        try
        {
            await SetStatusAsync(service, ServiceStatus.Generating);
            var workspace = WorkspacePath(service);
            GenerateFromTemplate(Options.TemplatePath, workspace, service.Name);
            AppendLog(service, $"Generated source code from the dotnet-api template in {workspace}");

            await SetStatusAsync(service, ServiceStatus.Building);
            await BuildImageAsync(service, workspace, ct);

            await SetStatusAsync(service, ServiceStatus.Starting);
            service.ContainerId = await StartContainerAsync(service, ct);
            service.HostPort = await GetHostPortAsync(service, ct);
            await SaveAsync(service);
            AppendLog(service, $"Container {service.ContainerName} published on 127.0.0.1:{service.HostPort}");

            // "Running" is only set after the service has proven it can answer requests.
            await WaitUntilHealthyAsync(service, ct);
            await SetStatusAsync(service, ServiceStatus.Running);
            AppendLog(service, $"Service is ready at {service.Url}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The platform is shutting down. Leave the record in its in-progress state;
            // RecoverAfterRestartAsync will mark it Failed and clean up on the next start.
            AppendLog(service, "Platform is shutting down; provisioning interrupted.");
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(service, ex.Message);
        }
    }

    /// <summary>Copies the bundled template into the workspace, filling in the service name.</summary>
    public static void GenerateFromTemplate(string templatePath, string workspacePath, string serviceName)
    {
        if (!ServiceRecord.IsValidName(serviceName))
            throw new ArgumentException($"Invalid service name '{serviceName}'.", nameof(serviceName));

        if (Directory.Exists(workspacePath))
            Directory.Delete(workspacePath, recursive: true);

        foreach (var sourceFile in Directory.EnumerateFiles(templatePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(templatePath, sourceFile);
            if (IsBuildOutput(relativePath)) continue;

            var targetFile = Path.Combine(workspacePath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            // All template files are small text files, so a plain string replace is enough.
            var content = File.ReadAllText(sourceFile).Replace(NamePlaceholder, serviceName);
            File.WriteAllText(targetFile, content);
        }
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private async Task BuildImageAsync(ServiceRecord service, string workspace, CancellationToken ct)
    {
        AppendLog(service, $"docker build --tag {service.ImageTag}");
        var result = await docker.RunAsync(
            ["build", "--progress=plain",
             "--tag", service.ImageTag,
             "--label", ManagedLabel,
             "--label", $"{ServiceIdLabel}={service.Id}",
             workspace],
            TimeSpan.FromSeconds(Options.BuildTimeoutSeconds),
            ct,
            onOutputLine: line => AppendLog(service, line, isCommandOutput: true)); // stream build output live

        if (!result.Succeeded)
            throw new DockerCommandException(
                $"Image build failed (exit code {result.ExitCode}): {LastLine(result.Diagnostics)}");
    }

    private async Task<string> StartContainerAsync(ServiceRecord service, CancellationToken ct)
    {
        AppendLog(service, $"docker run --name {service.ContainerName}");
        var result = await docker.RunAsync(
            ["run", "--detach",
             "--name", service.ContainerName,
             "--label", ManagedLabel,
             "--label", $"{ServiceIdLabel}={service.Id}",
             // "127.0.0.1::8080" = bind to localhost only, let Docker choose a free host port.
             "--publish", "127.0.0.1::8080",
             service.ImageTag],
            CommandTimeout, ct);

        if (!result.Succeeded)
            throw new DockerCommandException($"Container failed to start: {LastLine(result.Diagnostics)}");
        return result.StdOut.Trim();
    }

    /// <summary>Asks Docker which host port it assigned to the container's port 8080.</summary>
    private async Task<int> GetHostPortAsync(ServiceRecord service, CancellationToken ct)
    {
        var result = await docker.RunAsync(
            ["inspect", "--format", "{{json .NetworkSettings.Ports}}", service.ContainerName],
            CommandTimeout, ct);
        if (!result.Succeeded)
            throw new DockerCommandException($"Could not inspect container: {LastLine(result.Diagnostics)}");

        // Output looks like: {"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"55012"}]}
        using var json = JsonDocument.Parse(result.StdOut);
        if (json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("8080/tcp", out var bindings)
            && bindings.ValueKind == JsonValueKind.Array
            && bindings.GetArrayLength() > 0
            && int.TryParse(bindings[0].GetProperty("HostPort").GetString(), out var port))
        {
            return port;
        }
        throw new DockerCommandException("Docker did not report a host port for 8080/tcp. The container may have exited.");
    }

    private async Task WaitUntilHealthyAsync(ServiceRecord service, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{service.HostPort}/health";
        var timeout = TimeSpan.FromSeconds(Options.HealthCheckTimeoutSeconds);
        AppendLog(service, $"Waiting for {url} to return 200 (timeout {timeout.TotalSeconds:0}s)");

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow + timeout;
        var lastResult = "no response yet";

        while (true)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    AppendLog(service, "Health check passed");
                    return;
                }
                lastResult = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex) { lastResult = ex.Message; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { lastResult = "request timed out"; }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Health check did not pass within {timeout.TotalSeconds:0}s (last result: {lastResult}).");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <summary>Marks the service Failed, keeps diagnostics in the log, and removes partial resources.</summary>
    private async Task FailAsync(ServiceRecord service, string reason)
    {
        AppendLog(service, $"FAILED: {reason}");
        logger.LogWarning("Provisioning of {Service} failed: {Reason}", service.Name, reason);

        // If a container got as far as starting, its output usually explains the failure.
        // Copy it into the provisioning log before the container is removed.
        try
        {
            var containerLogs = await GetRuntimeLogsAsync(service, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(containerLogs))
            {
                AppendLog(service, $"Last container output (up to {RuntimeLogLines} lines):");
                foreach (var line in containerLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AppendLog(service, line, isCommandOutput: true);
            }
        }
        catch (Exception ex) { AppendLog(service, $"Could not read container logs: {ex.Message}"); }

        try
        {
            await RemoveOwnedResourcesAsync(service, CancellationToken.None);
            DeleteWorkspace(service);
            AppendLog(service, "Removed partially created resources. This log is kept for diagnosis.");
        }
        catch (Exception ex)
        {
            AppendLog(service, $"Cleanup incomplete ({ex.Message}). Deleting the service retries it.");
        }

        service.Status = ServiceStatus.Failed;
        service.ErrorMessage = reason;
        service.ContainerId = null;
        service.HostPort = null;
        await SaveAsync(service);
    }

    // ---------------------------------------------------------------- deletion & cleanup

    /// <summary>
    /// Removes the service's container, image, workspace and logs, then the database record.
    /// If Docker cleanup throws, the record stays so the user can retry.
    /// </summary>
    public async Task DeleteAsync(ServiceRecord service, CancellationToken ct)
    {
        await RemoveOwnedResourcesAsync(service, ct);
        DeleteWorkspace(service);
        File.Delete(LogPath(service));

        db.Services.Remove(service);
        await db.SaveChangesAsync(ct);
    }

    private async Task RemoveOwnedResourcesAsync(ServiceRecord service, CancellationToken ct)
    {
        // Containers are found by label rather than by the stored ContainerId, so a container
        // created just before a crash (whose ID was never saved) is still found.
        var containers = await RunCheckedAsync(
            ["ps", "--all", "--quiet", "--filter", $"label={ServiceIdLabel}={service.Id}"], ct);
        foreach (var containerId in SplitLines(containers.StdOut))
            await RunCheckedAsync(["rm", "--force", containerId], ct);

        // Images: only our exact tag. "image rm" on a tag never affects other images.
        var images = await RunCheckedAsync(["image", "ls", "--quiet", service.ImageTag], ct);
        if (SplitLines(images.StdOut).Length > 0)
            await RunCheckedAsync(["image", "rm", service.ImageTag], ct);
    }

    private void DeleteWorkspace(ServiceRecord service)
    {
        var workspace = WorkspacePath(service);
        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
    }

    // ---------------------------------------------------------------- restart recovery

    /// <summary>
    /// Runs once when the platform starts. Brings the database back in line with reality:
    /// interrupted provisioning becomes Failed, and Running/Stopped services are checked against Docker.
    /// Queued services are left alone — the worker simply picks them up.
    /// </summary>
    public async Task RecoverAfterRestartAsync(CancellationToken ct)
    {
        var services = await db.Services
            .Where(s => s.Status != ServiceStatus.Queued && s.Status != ServiceStatus.Failed)
            .ToListAsync(ct);

        foreach (var service in services)
        {
            if (service.IsProvisioning)
            {
                AppendLog(service, "Platform restarted while this service was being provisioned.");
                await FailAsync(service, $"Provisioning was interrupted by a platform restart (during {service.Status}).");
                continue;
            }

            var state = await GetContainerStateAsync(service, ct);
            var previous = service.Status;
            if (state is null)
            {
                service.Status = ServiceStatus.Failed;
                service.ErrorMessage = "The container no longer exists. Delete this service and create it again.";
                service.HostPort = null;
            }
            else if (state == "running")
            {
                service.Status = ServiceStatus.Running;
                service.ErrorMessage = null;
                service.HostPort = await GetHostPortAsync(service, ct); // a restarted container gets a new port
            }
            else
            {
                service.Status = ServiceStatus.Stopped;
                service.ErrorMessage = $"The container is no longer running (state: {state}). Delete this service and create it again.";
            }

            if (service.Status != previous)
                AppendLog(service, $"Platform restart: container is {state ?? "missing"}, status {previous} → {service.Status}");
            await SaveAsync(service);
        }
    }

    /// <summary>Returns the container state ("running", "exited", ...) or null when it doesn't exist.</summary>
    private async Task<string?> GetContainerStateAsync(ServiceRecord service, CancellationToken ct)
    {
        var result = await RunCheckedAsync(
            ["ps", "--all", "--filter", $"label={ServiceIdLabel}={service.Id}", "--format", "{{.State}}"], ct);
        return SplitLines(result.StdOut).FirstOrDefault();
    }

    // ---------------------------------------------------------------- logs

    /// <summary>The platform's own log of generating/building/starting this service.</summary>
    public string ReadProvisioningLog(ServiceRecord service, int maxLines = 1000)
    {
        var path = LogPath(service);
        if (!File.Exists(path)) return "";
        lock (LogFileLock)
        {
            var lines = File.ReadAllLines(path);
            return string.Join('\n', lines.TakeLast(maxLines));
        }
    }

    /// <summary>The latest stdout/stderr of the running container, or null if there is no container.</summary>
    public async Task<string?> GetRuntimeLogsAsync(ServiceRecord service, CancellationToken ct)
    {
        if (await GetContainerStateAsync(service, ct) is null) return null;

        // Collect stdout and stderr into one buffer, in the order lines arrive.
        var lines = new List<string>();
        var result = await docker.RunAsync(
            ["logs", "--tail", RuntimeLogLines.ToString(), service.ContainerName],
            CommandTimeout, ct,
            onOutputLine: line => { lock (lines) lines.Add(line); });
        if (!result.Succeeded)
            throw new DockerCommandException($"docker logs failed: {LastLine(result.Diagnostics)}");
        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- helpers

    private string WorkspacePath(ServiceRecord service) =>
        Path.Combine(Options.WorkspacesPath, service.Id.ToString("N"));

    private string LogPath(ServiceRecord service) =>
        Path.Combine(Options.LogsPath, $"{service.Id:N}.log");

    private void AppendLog(ServiceRecord service, string message, bool isCommandOutput = false)
    {
        // Platform messages get a timestamp; Docker output is indented beneath them.
        var line = isCommandOutput ? $"    {message}" : $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (LogFileLock)
        {
            Directory.CreateDirectory(Options.LogsPath);
            File.AppendAllText(LogPath(service), line + Environment.NewLine);
        }
    }

    private async Task SetStatusAsync(ServiceRecord service, ServiceStatus status)
    {
        service.Status = status;
        AppendLog(service, $"Status: {status}");
        await SaveAsync(service);
    }

    private async Task SaveAsync(ServiceRecord service)
    {
        service.UpdatedAt = DateTime.UtcNow;
        // Not cancellable on purpose: a status change should be recorded even during shutdown.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<DockerResult> RunCheckedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await docker.RunAsync(arguments, CommandTimeout, ct);
        if (!result.Succeeded)
            throw new DockerCommandException($"'docker {string.Join(' ', arguments)}' failed: {LastLine(result.Diagnostics)}");
        return result;
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string LastLine(string text) =>
        SplitLines(text).LastOrDefault() ?? "no output";
}
