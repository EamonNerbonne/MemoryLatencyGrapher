using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IncrementalMeanVarianceAccumulator;
using MemoryLatencyGrapher;

const long bytesPerPayload = 64;
const int innerLoopLength = 40_000;
const int maxTestingCountPerSize = 1000;
const int minTestingCountPerSize = 4;
const double targetRelativeError = 0.03;

var options = LatencyTestOptions.ParseOptions();
var timestampStr = DateTime.UtcNow.ToString("o").Replace(":", "_");
var basename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"latency_{timestampStr}");
using var writer = options.LogProgressToFile ? new StreamWriter(basename + ".log") : null;

Console.WriteLine("Using options (provide a json file parameter to alter): " + JsonSerializer.Serialize(options, ResultJsonSerializerContext.Default.LatencyTestOptions));

unsafe {
    var runTimePayloadSize = sizeof(payload_64byte);
    if (runTimePayloadSize != bytesPerPayload) {
        throw new($"Assumption violated; payload should be 64 bytes but is {runTimePayloadSize}");
    }
}

try {
    using (var proc = Process.GetCurrentProcess())
        proc.PriorityClass = ProcessPriorityClass.RealTime;
    Thread.CurrentThread.Priority = ThreadPriority.Highest;
} catch (Exception e) {
    Log("Unable to raise process priority. You may get more accurate results if you run this with increased priviledges (i.e. sudo).  Detailed message:" + e.Message);
}

var results = DoBenchmark(options, Log);
foreach (var result in results) {
    Log($"{result.Summarize()}    (final result for this size)");
}

options.SaveToSvg(results, basename + ".svg");
options.SaveResultsToJson(results, basename + ".json");

return;

void Log(string msg)
{
    if (options.LogProgressToConsole) {
        Console.WriteLine(msg);
    }
    writer?.WriteLine(msg);
}

static LatencyResult[] DoBenchmark(LatencyTestOptions latencyTestOptions, Action<string> log)
{
    var arr = new payload_64byte[latencyTestOptions.MaxMemoryInBytes / bytesPerPayload];
    IEnumerable<int> ArraySizes()
    {
        for (var target = Math.Max(1L << latencyTestOptions.MemorySizeGranularity, 2048 / bytesPerPayload); target < arr.Length; target += target >> latencyTestOptions.MemorySizeGranularity) {
            yield return (int)target;
        }
    }

    var arraySizes = ArraySizes().ToArray();
    var distributions = arraySizes.Select(_ => MeanVarianceAccumulator.Empty).ToArray();

    var overallTimer = Stopwatch.StartNew();
    for (var outerLoopIdx = 0; outerLoopIdx < latencyTestOptions.OuterLoopLength; outerLoopIdx++) {
        arr[0].i0 = 0;
        ExtendRandomCycle(arr, 1, 10);
        _ = RunTest(10, arr, 0); //pre-heat

        arr[0].i0 = 0;
        var idx = 0;
        var length = 1;
        for (var currentIter = 0; currentIter < arraySizes.Length; currentIter++) {
            var targetLength = arraySizes[currentIter];
            ExtendRandomCycle(arr, length, targetLength);
            length = targetLength;

            var (memLatencyNs, nextIdx) = RunTest(targetLength, arr, idx);
            idx = nextIdx;
            distributions[currentIter] = distributions[currentIter].Add(memLatencyNs);
            var result = new LatencyResult(targetLength * bytesPerPayload, memLatencyNs.Mean, StdError(memLatencyNs), memLatencyNs.WeightSum);
            log($"{result.Summarize()}   ({(outerLoopIdx * arraySizes.Length + currentIter) * 100.0 / arraySizes.Length / latencyTestOptions.OuterLoopLength:f1}% of test run complete)");
        }
    }
    log($"Benchmark took {overallTimer.Elapsed.TotalSeconds} seconds");
    return arraySizes.Zip(distributions, (cycleLength, nsDistribution) => (cycleLength, nsDistribution))
        .Select(o => new LatencyResult(o.cycleLength * bytesPerPayload, o.nsDistribution.Mean, StdError(o.nsDistribution), o.nsDistribution.WeightSum))
        .ToArray();
}
static void ExtendRandomCycle(payload_64byte[] payload64Bytes, int oldLength, long targetLength)
{
    var random = Random.Shared;

    for (; oldLength < targetLength; oldLength++) {
        var swapWith = random.Next(oldLength);
        payload64Bytes[oldLength].i0 = payload64Bytes[swapWith].i0;
        payload64Bytes[swapWith].i0 = oldLength;
    }
}
static double StdError(MeanVarianceAccumulator acc)
    => acc.SampleStandardDeviation / Math.Sqrt(acc.WeightSum);
static double RelativeError(MeanVarianceAccumulator acc)
    => StdError(acc) / acc.Mean;
[MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
static (MeanVarianceAccumulator memLatencyNs, int idx) RunTest(int length, Span<payload_64byte> arr, int idx)
{
    var memLatencyNs = MeanVarianceAccumulator.Empty;
    var netMinTestingCount = (int)(minTestingCountPerSize + (Math.Log(arr.Length) - Math.Log(length)) * 4);
    var nsPerTickPerLoop = 1000_000_000.0 / ((double)innerLoopLength * Stopwatch.Frequency);
    while (memLatencyNs.WeightSum < maxTestingCountPerSize
           && (memLatencyNs.WeightSum < netMinTestingCount
               || RelativeError(memLatencyNs) >= targetRelativeError)) {
        //avoid prefetching shenanigans:
        idx = arr[idx].i0;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < innerLoopLength; i++) {
            idx = arr[idx].i0;
        }
        var end = Stopwatch.GetTimestamp();
        memLatencyNs = memLatencyNs.Add((end - start) * nsPerTickPerLoop);
    }
    return (memLatencyNs, idx);
}

struct payload_64byte
{
    public int i0;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
    public int i1;
    public long L1, L2, L3, L4, L5, L6, L7;
#pragma warning restore CS0649 // Field is never assigned to, and will always have its default value
}
