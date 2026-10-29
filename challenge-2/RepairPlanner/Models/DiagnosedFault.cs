using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlannerAgent.Models;

public sealed class DiagnosedFault
{
    [JsonPropertyName("MachineId")]
    [JsonProperty("MachineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("FaultType")]
    [JsonProperty("FaultType")]
    public string FaultType { get; set; } = string.Empty;

    [JsonPropertyName("RootCause")]
    [JsonProperty("RootCause")]
    public string RootCause { get; set; } = string.Empty;

    [JsonPropertyName("Severity")]
    [JsonProperty("Severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("DetectedAt")]
    [JsonProperty("DetectedAt")]
    public DateTimeOffset DetectedAt { get; set; }

    [JsonPropertyName("Metadata")]
    [JsonProperty("Metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = [];
}
