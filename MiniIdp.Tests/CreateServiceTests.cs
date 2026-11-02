using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniIdp.Models;
using MiniIdp.Services;

namespace MiniIdp.Tests;

public class ServiceNameTests
{
    [Theory]
    [InlineData("orders-api")]
    [InlineData("abc")]
    [InlineData("a1-")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234")] // 30 characters, the maximum
    public void Accepts_valid_names(string name) => Assert.True(ServiceRecord.IsValidName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]                              // too short
    [InlineData("abcdefghijklmnopqrstuvwxyz12345")] // 31 characters
    [InlineData("Orders-api")]                      // uppercase
    [InlineData("1orders")]                         // starts with a digit
    [InlineData("-orders")]                         // starts with a hyphen
    [InlineData("orders_api")]
    [InlineData("orders api")]
    [InlineData("orders-api\n")]                    // trailing newline must not sneak past "$"
    [InlineData("../etc")]
    public void Rejects_invalid_names(string? name) => Assert.False(ServiceRecord.IsValidName(name));
}

public class CreateServiceTests
{
    [Fact]
    public async Task Valid_name_is_queued_and_returns_202()
    {
        await using var app = new TestApp();
        app.Docker.BuildGate = new TaskCompletionSource(); // keep the worker from finishing
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/services", new { name = "orders-api" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var service = await response.Content.ReadFromJsonAsync<ServiceRecord>(TestApp.Json);
        Assert.Equal("orders-api", service!.Name);
        Assert.Equal(ServiceStatus.Queued, service.Status);
        Assert.Equal($"/api/services/{service.Id}", response.Headers.Location?.ToString());
        app.Docker.BuildGate.SetResult();
    }

    [Fact]
    public async Task Invalid_name_returns_400_and_stores_nothing()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/services", new { name = "Orders API" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var services = await client.GetFromJsonAsync<List<ServiceRecord>>("/api/services", TestApp.Json);
        Assert.Empty(services!);
    }

    [Fact]
    public async Task Duplicate_name_returns_409()
    {
        await using var app = new TestApp();
        app.Docker.BuildGate = new TaskCompletionSource();
        var client = app.CreateClient();
        await app.CreateServiceAsync(client, "orders-api");

        var response = await client.PostAsJsonAsync("/api/services", new { name = "orders-api" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var services = await client.GetFromJsonAsync<List<ServiceRecord>>("/api/services", TestApp.Json);
        Assert.Single(services!);
        app.Docker.BuildGate.SetResult();
    }

    [Fact]
    public async Task Database_rejects_duplicate_names_even_without_the_api_check()
    {
        await using var app = new TestApp();
        await using var first = app.OpenDatabase();
        first.Services.Add(new ServiceRecord { Name = "orders-api" });
        await first.SaveChangesAsync();

        await using var second = app.OpenDatabase();
        second.Services.Add(new ServiceRecord { Name = "orders-api" });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }
}

public class TemplateTests
{
    [Fact]
    public void Generated_service_has_the_name_filled_in_everywhere()
    {
        var template = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "MiniIdp", "Templates", "dotnet-api"));
        var workspace = Path.Combine(Path.GetTempPath(), "mini-idp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ServiceProvisioner.GenerateFromTemplate(template, workspace, "orders-api");

            var files = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(workspace, f)).ToList();
            Assert.Contains("Dockerfile", files);
            Assert.Contains(".dockerignore", files);
            Assert.Contains("ServiceApi.csproj", files);

            Assert.Contains("\"orders-api\"", File.ReadAllText(Path.Combine(workspace, "Program.cs")));
            Assert.All(files, f => Assert.DoesNotContain(ServiceProvisioner.NamePlaceholder,
                File.ReadAllText(Path.Combine(workspace, f))));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
