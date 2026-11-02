using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniIdp.Data;
using MiniIdp.Models;
using MiniIdp.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration

builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection("Platform"));
builder.Services.PostConfigure<PlatformOptions>(o =>
{
    // Only the bundled template can be used; its location is fixed, never taken from a request.
    var contentRoot = builder.Environment.ContentRootPath;
    o.DataPath = Path.GetFullPath(o.DataDirectory, contentRoot);
    o.TemplatePath = Path.Combine(contentRoot, "Templates", "dotnet-api");
});

builder.Services.AddDbContext<PlatformDbContext>((sp, db) =>
{
    var dataPath = sp.GetRequiredService<IOptions<PlatformOptions>>().Value.DataPath;
    db.UseSqlite($"Data Source={Path.Combine(dataPath, "mini-idp.db")}");
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<DockerCli>();
builder.Services.AddScoped<ServiceProvisioner>();
builder.Services.AddHostedService<ProvisioningWorker>();

// Send statuses as "Running" rather than 4.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create the data folder and database on first run. EnsureCreated keeps the demo simple;
// a real platform would use EF Core migrations so the schema can evolve.
using (var scope = app.Services.CreateScope())
{
    var platform = scope.ServiceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value;
    if (!Directory.Exists(platform.TemplatePath))
        throw new InvalidOperationException($"Template not found at {platform.TemplatePath}");
    Directory.CreateDirectory(platform.DataPath);
    scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreated();
}

app.UseDefaultFiles(); // serves wwwroot/index.html at "/"
app.UseStaticFiles();


var api = app.MapGroup("/api/services");

api.MapPost("/", async (CreateServiceRequest request, PlatformDbContext db) =>
{
    var name = request.Name;
    if (!ServiceRecord.IsValidName(name))
        return Error(StatusCodes.Status400BadRequest,
            "Use 3–30 characters: lowercase letters, digits and hyphens, starting with a letter.");

    if (await db.Services.AnyAsync(s => s.Name == name))
        return Error(StatusCodes.Status409Conflict, $"A service named '{name}' already exists.");

    var service = new ServiceRecord { Name = name! };
    db.Services.Add(service);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Error(StatusCodes.Status409Conflict, $"A service named '{name}' already exists.");
    }

    return Results.Accepted($"/api/services/{service.Id}", service);
});

api.MapGet("/", async (PlatformDbContext db) =>
    await db.Services.OrderByDescending(s => s.CreatedAt).ToListAsync());

api.MapGet("/{id:guid}", async (Guid id, PlatformDbContext db) =>
    await db.Services.FindAsync(id) is { } service
        ? Results.Ok(service)
        : Error(StatusCodes.Status404NotFound, "Service not found."));

// Logs come from two places: the platform's own provisioning log, and the container's output.
api.MapGet("/{id:guid}/logs", async (Guid id, PlatformDbContext db, ServiceProvisioner provisioner, CancellationToken ct) =>
{
    if (await db.Services.FindAsync([id], ct) is not { } service)
        return Error(StatusCodes.Status404NotFound, "Service not found.");

    string? runtime = null;
    string? runtimeMessage = null;
    try
    {
        runtime = await provisioner.GetRuntimeLogsAsync(service, ct);
        if (runtime is null) runtimeMessage = "No container exists for this service.";
    }
    catch (DockerCommandException ex)
    {
        runtimeMessage = $"Could not read container logs: {ex.Message}";
    }

    return Results.Ok(new
    {
        provisioning = provisioner.ReadProvisioningLog(service),
        runtime,
        runtimeMessage,
        runtimeLineLimit = ServiceProvisioner.RuntimeLogLines,
    });
});

api.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, ServiceProvisioner provisioner, CancellationToken ct) =>
{
    if (await db.Services.FindAsync([id], ct) is not { } service)
        return Error(StatusCodes.Status404NotFound, "Service not found.");

    // While the worker owns the record, deleting it would race with the build. Keep it simple: refuse.
    if (service.IsProvisioning)
        return Error(StatusCodes.Status409Conflict,
            $"'{service.Name}' is still being provisioned ({service.Status}). Delete it once it is Running or Failed.");

    try
    {
        await provisioner.DeleteAsync(service, ct);
        return Results.NoContent();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // The record is kept, so the user can press Delete again once the problem is fixed.
        return Error(StatusCodes.Status500InternalServerError,
            $"Cleanup failed, the service was kept so you can retry: {ex.Message}");
    }
});

app.Run();

static IResult Error(int statusCode, string message) =>
    Results.Json(new { error = message }, statusCode: statusCode);

record CreateServiceRequest(string? Name);

// Lets the test project start the app with WebApplicationFactory<Program>.
public partial class Program;
