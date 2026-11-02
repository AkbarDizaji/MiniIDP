using System.Text.RegularExpressions;

namespace MiniIdp.Models;

/// <summary>
/// The lifecycle of a service. A new service walks Queued → Generating → Building → Starting → Running.
/// Any step can end in Failed. Stopped means a previously running container is no longer running.
/// </summary>
public enum ServiceStatus
{
    Queued,
    Generating,
    Building,
    Starting,
    Running,
    Failed,
    Stopped,
}

/// <summary>One row in the SQLite database: everything the platform knows about a service.</summary>
public partial class ServiceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? ContainerId { get; set; }
    public int? HostPort { get; set; }
    public string? ErrorMessage { get; set; }

    // The Docker names below are derived from the record instead of being stored.
    // That way cleanup can always find what it owns, even if provisioning crashed
    // before the container ID was saved.

    /// <summary>Short ID used in Docker names. The full ID is attached as a label.</summary>
    public string ShortId => Id.ToString("N")[..8];

    /// <summary>Image name: application namespace + service name + ID, e.g. mini-idp/orders-api-1a2b3c4d.</summary>
    public string ImageTag => $"mini-idp/{Name}-{ShortId}:latest";

    public string ContainerName => $"mini-idp-{Name}-{ShortId}";

    public string? Url => Status == ServiceStatus.Running && HostPort is int port
        ? $"http://127.0.0.1:{port}"
        : null;

    /// <summary>True while the worker owns this record. Such records cannot be deleted.</summary>
    public bool IsProvisioning => Status is ServiceStatus.Queued or ServiceStatus.Generating
        or ServiceStatus.Building or ServiceStatus.Starting;

    // Regex anchors: "$" would also match before a trailing newline ("orders-api\n"),
    // so we use "\z" (absolute end of string) to make the rule ^[a-z][a-z0-9-]{2,29}$ strict.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{2,29}\z")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);
}
