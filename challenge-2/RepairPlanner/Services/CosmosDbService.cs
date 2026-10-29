using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;

namespace RepairPlannerAgent.Services;

public sealed class CosmosDbService : IDisposable
{
    private const string TechniciansContainerName = "Technicians";
    private const string PartsContainerName = "PartsInventory";
    private const string WorkOrdersContainerName = "WorkOrders";

    private readonly CosmosClient _client;
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(
        string endpoint,
        string key,
        string databaseName,
        ILogger<CosmosDbService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(logger);

        _client = new CosmosClient(endpoint, key);
        var database = _client.GetDatabase(databaseName);
        _techniciansContainer = database.GetContainer(TechniciansContainerName);
        _partsContainer = database.GetContainer(PartsContainerName);
        _workOrdersContainer = database.GetContainer(WorkOrdersContainerName);
        _logger = logger;
    }

    public async Task<List<Technician>> GetAvailableTechniciansWithSkillsAsync(
        List<string> requiredSkills,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredSkills);

        var skills = requiredSkills
            .Where(skill => !string.IsNullOrWhiteSpace(skill))
            .Select(skill => skill.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (skills.Length == 0)
        {
            _logger.LogInformation("No required skills supplied; returning no technicians.");
            return [];
        }

        const string query =
            "SELECT DISTINCT VALUE c FROM c " +
            "JOIN skill IN c.skills " +
            "WHERE c.available = true AND ARRAY_CONTAINS(@requiredSkills, skill)";
        var queryDefinition = new QueryDefinition(query)
            .WithParameter("@requiredSkills", skills);
        var technicians = new List<Technician>();

        try
        {
            using FeedIterator<Technician> iterator =
                _techniciansContainer.GetItemQueryIterator<Technician>(queryDefinition);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                technicians.AddRange(response);
            }

            _logger.LogInformation(
                "Found {TechnicianCount} available technicians matching requested skills.",
                technicians.Count);
            return technicians;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to query available technicians from Cosmos DB.");
            throw;
        }
    }

    public async Task<List<Part>> GetPartsInventoryAsync(
        List<string> partNumbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partNumbers);

        var numbers = partNumbers
            .Where(partNumber => !string.IsNullOrWhiteSpace(partNumber))
            .Select(partNumber => partNumber.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (numbers.Length == 0)
        {
            _logger.LogInformation("No part numbers supplied; returning no inventory items.");
            return [];
        }

        const string query = "SELECT * FROM c WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)";
        var queryDefinition = new QueryDefinition(query)
            .WithParameter("@partNumbers", numbers);
        var parts = new List<Part>();

        try
        {
            using FeedIterator<Part> iterator =
                _partsContainer.GetItemQueryIterator<Part>(queryDefinition);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                parts.AddRange(response);
            }

            _logger.LogInformation("Fetched {PartCount} inventory items from Cosmos DB.", parts.Count);
            return parts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to query parts inventory from Cosmos DB.");
            throw;
        }
    }

    public async Task<WorkOrder> CreateWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        if (string.IsNullOrWhiteSpace(workOrder.Status))
        {
            throw new ArgumentException(
                "A work order status is required because it is the WorkOrders partition key.",
                nameof(workOrder));
        }

        if (string.IsNullOrWhiteSpace(workOrder.Id))
        {
            workOrder.Id = Guid.NewGuid().ToString();
        }

        try
        {
            var response = await _workOrdersContainer.CreateItemAsync(
                workOrder,
                new PartitionKey(workOrder.Status),
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Created work order {WorkOrderNumber} (id {WorkOrderId}, status {Status}).",
                workOrder.WorkOrderNumber,
                response.Resource.Id,
                workOrder.Status);
            return response.Resource;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to create work order {WorkOrderNumber} in Cosmos DB.",
                workOrder.WorkOrderNumber);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();
}
