var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var appVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "1.0.0";
var commitSha = Environment.GetEnvironmentVariable("APP_COMMIT_SHA") ?? "local-dev";
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
    machineName = Environment.MachineName,
    environment = app.Environment.EnvironmentName,
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
            new { status = "unhealthy", reason = "SIMULATE_UNHEALTHY=true (rollback verification mode)", version = appVersion },
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
