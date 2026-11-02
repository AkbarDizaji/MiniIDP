using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniIdp.Data;
using MiniIdp.Models;
using MiniIdp.Services;

namespace MiniIdp.Tests;

/// <summary>
/// Starts the real platform (endpoints, database, worker) in memory, with its own temp data
/// directory and a fake Docker. Each test creates its own instance.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "mini-idp-tests", Guid.NewGuid().ToString("N"));

    public FakeDocker Docker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Platform:DataDirectory", DataDirectory);
        builder.UseSetting("Platform:HealthCheckTimeoutSeconds", "1");
        builder.UseSetting("Platform:QueuePollIntervalSeconds", "0.05");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DockerCli>();
            services.AddSingleton<DockerCli>(Docker);
        });
    }

    /// <summary>A DbContext on the same SQLite file, usable before or after the app starts.</summary>
    public PlatformDbContext OpenDatabase()
    {
        Directory.CreateDirectory(DataDirectory);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(DataDirectory, "mini-idp.db")};Pooling=False")
            .Options;
        var db = new PlatformDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    public async Task<ServiceRecord> CreateServiceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/services", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceRecord>(Json))!;
    }

    public async Task<ServiceRecord?> GetServiceAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/services/{id}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await response.Content.ReadFromJsonAsync<ServiceRecord>(Json);
    }

    /// <summary>Polls until the service reaches a status for which <paramref name="done"/> is true.</summary>
    public async Task<ServiceRecord> WaitForAsync(HttpClient client, Guid id, Func<ServiceRecord, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var service = await GetServiceAsync(client, id);
            if (service is not null && done(service)) return service;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Service stayed in status {service?.Status}.");
            await Task.Delay(50);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Stands in for the Docker CLI. It understands just enough of the commands the platform runs
/// to simulate one service's container and image, and records every call for assertions.
/// </summary>
public sealed class FakeDocker() : DockerCli(Options.Create(new PlatformOptions()), NullLogger<DockerCli>.Instance)
{
    private readonly Lock gate = new();
    public List<string[]> Calls { get; } = [];

    public bool ImageExists { get; set; }
    public string? ContainerState { get; set; } // null = no container

    /// <summary>The host port reported by "docker inspect". Port 1 has nothing listening.</summary>
    public int HostPort { get; set; } = 1;

    /// <summary>Docker commands (e.g. "build", "rm") that should fail with exit code 1.</summary>
    public HashSet<string> FailingCommands { get; } = [];

    /// <summary>When set, "docker build" waits for this before finishing.</summary>
    public TaskCompletionSource? BuildGate { get; set; }

    public bool WasCalled(string command) => CallsTo(command).Any();
    public IEnumerable<string[]> CallsTo(string command)
    {
        lock (gate) return Calls.Where(c => c[0] == command).ToList();
    }

    public override async Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? onOutputLine = null)
    {
        var command = arguments[0];
        lock (gate) Calls.Add([.. arguments]);

        if (FailingCommands.Contains(command))
        {
            onOutputLine?.Invoke($"simulated {command} output");
            return new DockerResult(1, "", $"simulated {command} failure");
        }

        switch (command)
        {
            case "build":
                onOutputLine?.Invoke("#1 simulated build step");
                if (BuildGate is not null) await BuildGate.Task.WaitAsync(cancellationToken);
                ImageExists = true;
                return Ok();
            case "run":
                ContainerState = "running";
                return Ok("fakecontainerid\n");
            case "inspect":
                return Ok($$"""{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"{{HostPort}}"}]}""");
            case "ps" when arguments.Contains("--quiet"):
                return Ok(ContainerState is null ? "" : "fakecontainerid\n");
            case "ps":
                return Ok(ContainerState is null ? "" : ContainerState + "\n");
            case "rm":
                ContainerState = null;
                return Ok();
            case "logs":
                onOutputLine?.Invoke("simulated container output");
                return Ok();
            case "image" when arguments[1] == "ls":
                return Ok(ImageExists ? "sha256:fake\n" : "");
            case "image" when arguments[1] == "rm":
                ImageExists = false;
                return Ok();
            default:
                throw new InvalidOperationException($"Unexpected docker command: {string.Join(' ', arguments)}");
        }
    }

    private static DockerResult Ok(string stdout = "") => new(0, stdout, "");
}
