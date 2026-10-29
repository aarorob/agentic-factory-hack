using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger("Program");

try
{
    var projectEndpoint = GetRequiredEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT");
    var modelDeploymentName = GetRequiredEnvironmentVariable("MODEL_DEPLOYMENT_NAME");
    var cosmosEndpoint = GetRequiredEnvironmentVariable("COSMOS_ENDPOINT");
    var cosmosKey = GetRequiredEnvironmentVariable("COSMOS_KEY");
    var cosmosDatabaseName = GetRequiredEnvironmentVariable("COSMOS_DATABASE_NAME");

    var projectClient = new AIProjectClient(
        new Uri(projectEndpoint),
        new DefaultAzureCredential());
    using var cosmosDb = new CosmosDbService(
        cosmosEndpoint,
        cosmosKey,
        cosmosDatabaseName,
        loggerFactory.CreateLogger<CosmosDbService>());

    var repairPlanner = new global::RepairPlannerAgent.RepairPlannerAgent(
        projectClient,
        cosmosDb,
        new FaultMappingService(),
        modelDeploymentName,
        loggerFactory.CreateLogger<global::RepairPlannerAgent.RepairPlannerAgent>());

    var sampleFault = new DiagnosedFault
    {
        MachineId = "machine-001",
        FaultType = "curing_temperature_excessive",
        RootCause = "The curing press temperature is above its operating threshold.",
        Severity = "High",
        DetectedAt = DateTimeOffset.UtcNow,
        Metadata = new Dictionary<string, object?>
        {
            ["MostLikelyRootCauses"] = new[]
            {
                "Heating element malfunction",
                "Temperature sensor drift"
            }
        }
    };

    logger.LogInformation(
        "Starting repair planning for machine {MachineId}, fault {FaultType}.",
        sampleFault.MachineId,
        sampleFault.FaultType);

    var workOrder = await repairPlanner.PlanAndCreateWorkOrderAsync(sampleFault);
    Console.WriteLine(JsonSerializer.Serialize(workOrder, new JsonSerializerOptions
    {
        WriteIndented = true
    }));
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Repair planning workflow failed.");
    Environment.ExitCode = 1;
}

static string GetRequiredEnvironmentVariable(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
    }

    return value;
}
