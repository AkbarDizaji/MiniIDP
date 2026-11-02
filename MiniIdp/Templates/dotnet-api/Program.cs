using System.Diagnostics;

// Mini IDP replaces this placeholder with the real service name when it generates the service.
// It is an ordinary string, so the template also compiles as-is.
const string ServiceName = "__SERVICE_NAME__";

var builder = WebApplication.CreateBuilder(args);

// One readable line per log entry, so `docker logs` output is easy to follow.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = builder.Build();

// Request logging: method, path, status code and duration for every request.
app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(context);
    app.Logger.LogInformation("{Method} {Path} -> {StatusCode} in {Elapsed} ms",
        context.Request.Method, context.Request.Path, context.Response.StatusCode,
        stopwatch.ElapsedMilliseconds);
});

app.MapGet("/", () => new
{
    service = ServiceName,
    message = $"Hello from {ServiceName}!",
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Logger.LogInformation("{ServiceName} starting", ServiceName);
app.Run();
