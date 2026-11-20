using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Read build-time release-metadata.json when present; explicit environment variables take precedence
string? metaVersion = null;
string? metaCommitSha = null;
string? metaBuiltAtUtc = null;
var metadataPath = Path.Combine(AppContext.BaseDirectory, "release-metadata.json");
if (File.Exists(metadataPath))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            metaVersion = v.GetString();
        if (doc.RootElement.TryGetProperty("commitSha", out var c) && c.ValueKind == JsonValueKind.String)
            metaCommitSha = c.GetString();
        if (doc.RootElement.TryGetProperty("builtAtUtc", out var b) && b.ValueKind == JsonValueKind.String)
            metaBuiltAtUtc = b.GetString();
    }
    catch
    {
        // Ignore malformed optional metadata and fall back to environment/defaults
    }
}

var appVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? metaVersion ?? "1.0.0";
var commitSha = Environment.GetEnvironmentVariable("APP_COMMIT_SHA") ?? metaCommitSha ?? "local-dev";
var dataDir = Environment.GetEnvironmentVariable("PERSISTENT_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "local-data");
Directory.CreateDirectory(dataDir);

var markerPath = Path.Combine(dataDir, "persistent-marker.txt");
if (!File.Exists(markerPath))
{
    File.WriteAllText(markerPath, $"initialized:{DateTimeOffset.UtcNow:O}:{Guid.NewGuid():N}");
}

app.MapGet("/", () => Results.Ok(new
{
    service = "ServerForge Disposable E2E Sample App",
    version = appVersion,
    commitSha,
    builtAtUtc = metaBuiltAtUtc,
    machineName = Environment.MachineName,
    environment = app.Environment.EnvironmentName,
    processId = Environment.ProcessId,
    timestampUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () =>
{
    var simulateUnhealthy = string.Equals(
        Environment.GetEnvironmentVariable("SIMULATE_UNHEALTHY"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (simulateUnhealthy)
    {
        return Results.Json(
            new
            {
                status = "unhealthy",
                reason = "SIMULATE_UNHEALTHY=true (rollback verification mode)",
                version = appVersion,
                commitSha
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        status = "healthy",
        version = appVersion,
        commitSha,
        uptimeSeconds = Environment.TickCount64 / 1000
    });
});

app.MapGet("/data", () => Results.Ok(new
{
    persistentDataDirectory = dataDir,
    markerExists = File.Exists(markerPath),
    markerContent = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null
}));

app.Run();
