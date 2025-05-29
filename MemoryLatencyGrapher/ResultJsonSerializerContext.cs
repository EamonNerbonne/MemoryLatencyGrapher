using System.Text.Json.Serialization;

namespace MemoryLatencyGrapher;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LatencyResult))]
[JsonSerializable(typeof(LatencyResult[]))]
public partial class ResultJsonSerializerContext : JsonSerializerContext { }
