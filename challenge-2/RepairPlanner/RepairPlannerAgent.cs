using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.Logging;
using RepairPlannerAgent.Models;
using RepairPlannerAgent.Services;

namespace RepairPlannerAgent;

public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";
    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Create a repair plan using only the supplied diagnosed fault, technician candidates,
        and parts inventory. Assign only an available candidate technician, or null if none
        is suitable. Use only supplied inventory items for parts.
        Return exactly one valid JSON object matching the WorkOrder schema, with these fields:
        workOrderNumber, machineId, title, description, type, priority, status, assignedTo,
        estimatedDuration, partsUsed, tasks, notes.
        type must be "corrective", "preventive", or "emergency".
        priority must be "critical", "high", "medium", or "low".
        status should be "new".
        partsUsed must contain objects with partId, partNumber, and quantity.
        tasks must contain objects with sequence, title, description,
        estimatedDurationMinutes, requiredSkills, and safetyNotes.
        All duration fields must be integer minutes, not strings.
        Order tasks by sequence and make each task actionable.
        Do not include Markdown or text outside the JSON object.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private ProjectsAgentVersion? _agentVersion;
    private bool _agentVersionEnsured;

    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        await _registrationLock.WaitAsync(ct);
        try
        {
            if (_agentVersionEnsured)
            {
                return;
            }

            logger.LogInformation(
                "Creating agent {AgentName} with model {ModelDeploymentName}.",
                AgentName,
                modelDeploymentName);

            var definition = new DeclarativeAgentDefinition(model: modelDeploymentName)
            {
                Instructions = AgentInstructions
            };

            _agentVersion = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
                agentName: AgentName,
                options: new ProjectsAgentVersionCreationOptions(definition),
                cancellationToken: ct);

            _agentVersionEnsured = true;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentException.ThrowIfNullOrWhiteSpace(fault.FaultType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fault.MachineId);

        await EnsureAgentVersionAsync(ct);

        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        logger.LogInformation(
            "Planning repair for machine {MachineId}, fault {FaultType}.",
            fault.MachineId,
            fault.FaultType);

        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(
            requiredSkills.ToList(),
            ct);
        var parts = await cosmosDb.GetPartsInventoryAsync(requiredPartNumbers.ToList(), ct);

        var planningContext = JsonSerializer.Serialize(new
        {
            diagnosedFault = fault,
            requiredSkills,
            availableTechnicians = technicians,
            availableParts = parts
        });

        var userPrompt = $"""
            Create a repair work order using this planning context:
            {planningContext}
            """;

            logger.LogInformation("Invoking agent {AgentName} to generate the work order.", AgentName);
            var agentVersion = _agentVersion
                ?? throw new InvalidOperationException("The Repair Planner Agent version was not registered.");
            var agent = projectClient.AsAIAgent(agentVersion);
            var response = await agent.RunAsync(
                userPrompt,
                session: null,
                options: null,
                cancellationToken: ct);

        WorkOrder workOrder;
        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(response.Text ?? string.Empty, JsonOptions)
                ?? throw new JsonException("The agent returned an empty work order.");
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Agent {AgentName} returned invalid work-order JSON.", AgentName);
            throw new InvalidOperationException(
                "The Repair Planner Agent returned a response that could not be parsed as a work order.",
                exception);
        }

        ApplyWorkOrderDefaults(workOrder, fault, technicians);

        var savedWorkOrder = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);
        logger.LogInformation(
            "Saved work order {WorkOrderNumber} (id {WorkOrderId}, status {Status}).",
            savedWorkOrder.WorkOrderNumber,
            savedWorkOrder.Id,
            savedWorkOrder.Status);

        return savedWorkOrder;
    }

    private static void ApplyWorkOrderDefaults(
        WorkOrder workOrder,
        DiagnosedFault fault,
        IReadOnlyCollection<Technician> availableTechnicians)
    {
        workOrder.WorkOrderNumber = string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber)
            ? $"WO-{Guid.NewGuid():N}"
            : workOrder.WorkOrderNumber;
        workOrder.MachineId = string.IsNullOrWhiteSpace(workOrder.MachineId)
            ? fault.MachineId
            : workOrder.MachineId;
        workOrder.Title ??= "Repair diagnosed equipment fault";
        workOrder.Description ??= fault.RootCause;
        workOrder.Type = string.IsNullOrWhiteSpace(workOrder.Type) ? "corrective" : workOrder.Type;
        workOrder.Priority = string.IsNullOrWhiteSpace(workOrder.Priority) ? "medium" : workOrder.Priority;
        workOrder.Status = string.IsNullOrWhiteSpace(workOrder.Status) ? "new" : workOrder.Status;
        workOrder.CreatedDate ??= DateTimeOffset.UtcNow;
        workOrder.EstimatedDuration = workOrder.EstimatedDuration > 0
            ? workOrder.EstimatedDuration
            : 60;
        workOrder.PartsUsed ??= [];
        workOrder.Tasks ??= [];
        workOrder.Notes ??= string.Empty;

        if (!string.IsNullOrWhiteSpace(workOrder.AssignedTo)
            && !availableTechnicians.Any(technician =>
                string.Equals(technician.Id, workOrder.AssignedTo, StringComparison.OrdinalIgnoreCase)))
        {
            workOrder.AssignedTo = null;
        }
    }
}
