using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IncrementalMeanVarianceAccumulator;
using MemoryLatencyGrapher;
using VectSharp.Plots;
using VectSharp.SVG;

const long maxMemory = 1L << 30;
const long bytesPerPayload = 64;
const long payloadCount = maxMemory / bytesPerPayload; //longs are 2^3 bytes
const int shift = 3; // x-axis granularity; 0 is just factors of two, 1 is very rough, 4 is pretty detailed, 6 is probably beyond reasonable.
const int innerLoopLength = 30_000;
const int maxTestingCountPerSize = 1_000;
const int minTestingCountPerSize = 10;
const int outerLoopLength = 40; //how many times to run the inner loop before measuring the time
const double target_relative_error = 0.15; //0.002;
const double target_absolute_error = 0.4; // 0.02;

unsafe {
    var runTimePayloadSize = sizeof(payload_64byte);
    if (runTimePayloadSize != bytesPerPayload) {
        throw new($"Assumption violated; payload should be 64 bytes but is {runTimePayloadSize}");
    }
}

var timestampStr = DateTime.UtcNow.ToString("o").Replace(":", "_");
var basename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"latency_{timestampStr}");
using var writer = new StreamWriter(basename + ".log");
void Log(string msg)
{
    Console.WriteLine(msg);
    writer.WriteLine(msg);
}

var arr = new payload_64byte[payloadCount];
var rnd = Random.Shared;
var sw = new Stopwatch();

try {
    using (var proc = Process.GetCurrentProcess())
        proc.PriorityClass = ProcessPriorityClass.RealTime;

    Thread.CurrentThread.Priority = ThreadPriority.Highest;
} catch (Exception e) {
    Log("Unable to raise process priority. You may get more accurate results if you run this with increased priviledges (i.e. sudo).  Detailed message:" + e.Message);
}

_ = RunTest(1, sw, arr, 0);
IEnumerable<long> ArraySizes()
{
    for (var target = Math.Max(1L << shift, 2048 / bytesPerPayload); target < payloadCount; target += target >> shift) {
        yield return target;
    }
}

var arraySizes = ArraySizes().ToArray();
var distributions = arraySizes.Select(_ => MeanVarianceAccumulator.Empty).ToArray();
var results = new List<LatencyResult>();

for (var outerLoopIdx = 0; outerLoopIdx < outerLoopLength; outerLoopIdx++) {
    var length = 1;
    long idx = 0;
    arr[0].v0 = 0;
    for (var currentIter = 0; currentIter < arraySizes.Length; currentIter++) {
        var target = arraySizes[currentIter];
        for (; length < target; length++) {
            var swapWith = rnd.Next(length);
            arr[length].v0 = arr[swapWith].v0;
            arr[swapWith].v0 = length;
        }

        var (memLatencyNs, nextIdx) = RunTest((int)target, sw, arr, idx);
        idx = nextIdx;
        distributions[currentIter] = distributions[currentIter].Add(memLatencyNs);
        var result = new LatencyResult(target * bytesPerPayload, memLatencyNs.Mean, StdError(memLatencyNs), memLatencyNs.WeightSum);
        Log($"{result.MemorySizeInBytes} bytes: {result.latency_ns:f2}ns +/- {result.latency_stderr_ns:f4}     ({(outerLoopIdx * arraySizes.Length + currentIter) * 100.0 / arraySizes.Length / outerLoopLength:f1}% of test run complete)");
    }
}
for (var currentIter = 0; currentIter < arraySizes.Length; currentIter++) {
    var target = arraySizes[currentIter];
    var memLatencyNs = distributions[currentIter];
    var result = new LatencyResult(target * bytesPerPayload, memLatencyNs.Mean, StdError(memLatencyNs), memLatencyNs.WeightSum);
    results.Add(result);
    Log($"{result.MemorySizeInBytes} bytes: {result.latency_ns:f2}ns +/- {result.latency_stderr_ns:f4}     (final result for this size)");
}

var Ymax = results.Select(r => r.Y_plus2stderr).Max();
var Ymin = Math.Max(0.1, results.Select(r => r.Y_min2stderr).Min());
var Xmax = results.Select(r => r.X).Max();
var Xmin = results.Select(r => r.X).Min();
//var system = new LinLogCoordinateSystem2D(Xmin, Xmax, Ymin, Ymax, 1800, 1000);
//var system = new LinLogCoordinateSystem2D(Xmin, Xmax, 0.0, Ymax, 1800, 1000);
var system = new LogarithmicCoordinateSystem2D(Xmin, Xmax, Ymin, Ymax, 1800, 1000);

var renderedPlot = Plot.Create.LineCharts(
    [
        results.Select(r => (r.X, r.Y)).ToArray(),
        results.Select(r => (r.X, r.Y_min2stderr)).ToArray(),
        results.Select(r => (r.X, r.Y_plus2stderr)).ToArray(),
    ],
    width: 1800,
    height: 1000,
    xAxisTitle: "bytes",
    yAxisTitle: "latency (ps)",
    coordinateSystem: system
).Render();
var svgName = basename + ".svg";
renderedPlot.SaveAsSVG(svgName);
Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + svgName + "\"" });

using var jsonStream = File.OpenWrite(basename + ".json");
JsonSerializer.Serialize(jsonStream, results.ToArray(), ResultJsonSerializerContext.Default.LatencyResultArray);
return;

static (MeanVarianceAccumulator memLatencyNs, long idx) RunTest(int length, Stopwatch sw, payload_64byte[] arr, long idx)
{
    var memLatencyNs = MeanVarianceAccumulator.Empty;
    var netMinTestingCount = (int)(minTestingCountPerSize + (Math.Log(payloadCount) - Math.Log(length)) * 20);
    while (memLatencyNs.WeightSum < maxTestingCountPerSize
           && (memLatencyNs.WeightSum < netMinTestingCount
               || RelativeError(memLatencyNs) >= target_relative_error
               || StdError(memLatencyNs) >= target_absolute_error)) {
        idx = arr[idx].v0; //avoid prefetching shenanigans
        sw.Restart();
        for (var i = 0; i < innerLoopLength; i++) {
            idx = arr[idx].v0;
        }
        memLatencyNs = memLatencyNs.Add(sw.Elapsed.TotalNanoseconds / innerLoopLength);
    }
    return (memLatencyNs, idx);
}
static double StdError(MeanVarianceAccumulator acc)
    => acc.SampleStandardDeviation / Math.Sqrt(acc.WeightSum);
static double RelativeError(MeanVarianceAccumulator acc)
    => StdError(acc) / acc.Mean;

struct payload_64byte
{
    public long v0;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
    public long v1, v2, v3, v4, v5, v6, v7;
#pragma warning restore CS0649 // Field is never assigned to, and will always have its default value
}
