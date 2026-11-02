namespace MiniIdp.Services;

/// <summary>Settings from the "Platform" section of appsettings.json.</summary>
public class PlatformOptions
{
    /// <summary>Where the database, workspaces and logs live. Relative paths are resolved against the content root.</summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>The Docker CLI executable. Always a fixed setting, never user input.</summary>
    public string DockerPath { get; set; } = "docker";

    public int BuildTimeoutSeconds { get; set; } = 600;
    public int HealthCheckTimeoutSeconds { get; set; } = 60;
    public double QueuePollIntervalSeconds { get; set; } = 1;

    // Resolved absolute paths, filled in by Program.cs at startup.
    public string DataPath { get; set; } = "";
    public string TemplatePath { get; set; } = "";

    public string WorkspacesPath => Path.Combine(DataPath, "workspaces");
    public string LogsPath => Path.Combine(DataPath, "logs");
}
