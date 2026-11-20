using Serilog;
using ServerForge.Agent;

try
{
    await using var handle = await AgentHostFactory.CreateAndInitializeAsync(args);
    await handle.App.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ServerForge Agent terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
