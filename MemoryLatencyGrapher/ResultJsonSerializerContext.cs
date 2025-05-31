using System.Text.Json.Serialization;

namespace MemoryLatencyGrapher;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LatencyResult))]
[JsonSerializable(typeof(LatencyTestOptions))]
[JsonSerializable(typeof(LatencyResult[]))]
public sealed partial class ResultJsonSerializerContext : JsonSerializerContext { }