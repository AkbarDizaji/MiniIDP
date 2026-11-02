using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using MiniIdp.Models;

namespace MiniIdp.Tests;

public class LifecycleTests
{
    [Fact]
    public async Task Service_becomes_running_only_after_its_health_check_passes()
    {
        using var healthServer = HealthServer.Start();
        await using var app = new TestApp();
        app.Docker.HostPort = healthServer.Port;
        var client = app.CreateClient();

        var created = await app.CreateServiceAsync(client, "orders-api");
        var service = await app.WaitForAsync(client, created.Id, s => !s.IsProvisioning);

        Assert.Equal(ServiceStatus.Running, service.Status);
        Assert.Equal($"http://127.0.0.1:{healthServer.Port}", service.Url);
        Assert.True(healthServer.Requests > 0);

        // The container is published on localhost only and labelled with its owner.
        var run = Assert.Single(app.Docker.CallsTo("run"));
        Assert.Contains("127.0.0.1::8080", run);
        Assert.Contains($"mini-idp.service-id={created.Id}", run);
    }

    [Fact]
    public async Task Build_failure_marks_service_failed_and_keeps_the_build_log()
    {
        await using var app = new TestApp();
        app.Docker.FailingCommands.Add("build");
        var client = app.CreateClient();

        var created = await app.CreateServiceAsync(client, "orders-api");
        var service = await app.WaitForAsync(client, created.Id, s => !s.IsProvisioning);

        Assert.Equal(ServiceStatus.Failed, service.Status);
        Assert.Contains("Image build failed", service.ErrorMessage);
        Assert.False(app.Docker.WasCalled("run"));

        var logs = await client.GetFromJsonAsync<LogsResponse>($"/api/services/{created.Id}/logs");
        Assert.Contains("simulated build output", logs!.Provisioning);
        Assert.Contains("FAILED", logs.Provisioning);
    }

    [Fact]
    public async Task Unhealthy_service_fails_and_its_container_and_image_are_removed()
    {
        await using var app = new TestApp();
        app.Docker.HostPort = 1; // nothing listens there, so /health never answers
        var client = app.CreateClient();

        var created = await app.CreateServiceAsync(client, "orders-api");
        var service = await app.WaitForAsync(client, created.Id, s => !s.IsProvisioning);

        Assert.Equal(ServiceStatus.Failed, service.Status);
        Assert.Contains("Health check did not pass", service.ErrorMessage);
        Assert.Null(service.Url);
        Assert.Null(app.Docker.ContainerState);
        Assert.False(app.Docker.ImageExists);
        Assert.False(Directory.Exists(Path.Combine(app.DataDirectory, "workspaces", created.Id.ToString("N"))));

        // The container's output was copied into the provisioning log before the container was removed.
        var logs = await client.GetFromJsonAsync<LogsResponse>($"/api/services/{created.Id}/logs");
        Assert.Contains("simulated container output", logs!.Provisioning);
    }

    [Fact]
    public async Task Delete_is_refused_while_provisioning()
    {
        await using var app = new TestApp();
        app.Docker.BuildGate = new TaskCompletionSource();
        var client = app.CreateClient();
        var created = await app.CreateServiceAsync(client, "orders-api");
        await app.WaitForAsync(client, created.Id, s => s.Status == ServiceStatus.Building);

        var response = await client.DeleteAsync($"/api/services/{created.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(await app.GetServiceAsync(client, created.Id));
        app.Docker.BuildGate.SetResult();
    }

    [Fact]
    public async Task Failed_cleanup_keeps_the_record_so_delete_can_be_retried()
    {
        using var healthServer = HealthServer.Start();
        await using var app = new TestApp();
        app.Docker.HostPort = healthServer.Port;
        var client = app.CreateClient();
        var created = await app.CreateServiceAsync(client, "orders-api");
        await app.WaitForAsync(client, created.Id, s => s.Status == ServiceStatus.Running);

        app.Docker.FailingCommands.Add("rm");
        var failed = await client.DeleteAsync($"/api/services/{created.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.NotNull(await app.GetServiceAsync(client, created.Id));

        app.Docker.FailingCommands.Clear();
        var retried = await client.DeleteAsync($"/api/services/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
        Assert.Null(await app.GetServiceAsync(client, created.Id));
        Assert.Null(app.Docker.ContainerState);
        Assert.False(app.Docker.ImageExists);
    }

    [Fact]
    public async Task Restart_fails_interrupted_work_reconciles_running_services_and_resumes_the_queue()
    {
        await using var app = new TestApp();
        // State left behind by a previous run: an image from an interrupted build, but no containers.
        app.Docker.ImageExists = true;
        var interrupted = new ServiceRecord { Name = "catalog-api", Status = ServiceStatus.Building };
        var vanished = new ServiceRecord { Name = "billing-api", Status = ServiceStatus.Running, HostPort = 5000 };
        var queued = new ServiceRecord { Name = "queued-api", Status = ServiceStatus.Queued };
        await using (var db = app.OpenDatabase())
        {
            db.Services.AddRange(interrupted, vanished, queued);
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient(); // starts the platform, which runs recovery

        var afterInterrupt = await app.WaitForAsync(client, interrupted.Id, s => s.Status == ServiceStatus.Failed);
        Assert.Contains("interrupted", afterInterrupt.ErrorMessage);
        Assert.Contains(app.Docker.CallsTo("image"), c => c[1] == "rm" && c[2] == interrupted.ImageTag);

        var afterVanish = await app.WaitForAsync(client, vanished.Id, s => s.Status != ServiceStatus.Running);
        Assert.Equal(ServiceStatus.Failed, afterVanish.Status);
        Assert.Contains("no longer exists", afterVanish.ErrorMessage);

        // The queued service is still processed; it is not left behind or skipped.
        await app.WaitForAsync(client, queued.Id, s => !s.IsProvisioning);
        Assert.Contains(app.Docker.CallsTo("build"), c => c.Contains(queued.ImageTag));
    }

    [Fact]
    public async Task Restart_marks_a_running_service_stopped_when_its_container_has_exited()
    {
        await using var app = new TestApp();
        app.Docker.ContainerState = "exited";
        var service = new ServiceRecord { Name = "orders-api", Status = ServiceStatus.Running, HostPort = 5000 };
        await using (var db = app.OpenDatabase())
        {
            db.Services.Add(service);
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var after = await app.WaitForAsync(client, service.Id, s => s.Status != ServiceStatus.Running);

        Assert.Equal(ServiceStatus.Stopped, after.Status);
        Assert.Null(after.Url);
        Assert.False(app.Docker.WasCalled("build")); // existing services are never rebuilt automatically
    }

    private record LogsResponse(string Provisioning, string? Runtime);

    /// <summary>A tiny HTTP server on 127.0.0.1 that answers every request with 200, standing in for /health.</summary>
    private sealed class HealthServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private int requests;
        public int Port { get; }
        public int Requests => requests;

        private HealthServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        Interlocked.Increment(ref requests);
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                    catch (Exception) { return; } // listener stopped
                }
            });
        }

        public static HealthServer Start() => new();
        public void Dispose() => listener.Close();
    }
}
