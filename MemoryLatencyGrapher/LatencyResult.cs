namespace MemoryLatencyGrapher;

public sealed record LatencyResult(long MemorySizeInBytes, double latency_ns, double latency_stderr_ns, double TrialCount)
{
    public double X
        => MemorySizeInBytes;

    public double Y_min2stderr
        => (latency_ns - 2 * latency_stderr_ns) * 1000.0;

    public double Y_plus2stderr
        => (latency_ns + 2 * latency_stderr_ns) * 1000.0;

    public double Y
        => latency_ns * 1000.0;

    public string Summarize()
        => $"{MemorySizeInBytes,10} bytes: {latency_ns,5:f2}ns +/- {latency_stderr_ns,5:f4}; {latency_stderr_ns / latency_ns * 100,4:f1}% after {TrialCount:f0} runs";
}
