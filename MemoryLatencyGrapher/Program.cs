using System.Diagnostics;
using System.Text.Json;
using IncrementalMeanVarianceAccumulator;
using MemoryLatencyGrapher;
using VectSharp.Plots;
using VectSharp.SVG;

var timestampStr = DateTime.UtcNow.ToString("o").Replace(":", "_");
var basename = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"latency_{timestampStr}");
using var writer = new StreamWriter(basename + ".log");
void Log(string msg)
{
    Console.WriteLine(msg);
    writer.WriteLine(msg);
}

const long maxMemory = 1L << 30;
const long maxLong = maxMemory >> 3; //longs are 2^3 bytes
const int shift = 2; // x-axis granularity; 0 is just factors of two, 1 is very rough, 4 is pretty detailed, 6 is probably beyond reasonable.
const int innerLoopLength = 30_000;
const int maxTestingCountPerSize = 10_000;
const int minTestingCountPerSize = 40;
const double target_relative_error = 0.05; //0.002;
const double target_absolute_error = 0.1; // 0.02;

var arr = new long[maxLong];
var rnd = Random.Shared;
var sw = new Stopwatch();

var results = new List<LatencyResult>();
using (var proc = Process.GetCurrentProcess())
    proc.PriorityClass = ProcessPriorityClass.RealTime;

Thread.CurrentThread.Priority = ThreadPriority.Highest;

var length = 1;
long idx = 0;
RunTest(length, sw, arr, ref idx); //precompile
var totalIters = 0;
for (var target = Math.Max(1L << shift, 256); target < maxLong; target += target >> shift) {
    totalIters++;
}
var currentIter = 0;
for (var target = Math.Max(1L << shift, 256); target < maxLong; target += target >> shift) {
    for (; length < target; length++) {
        var swapWith = rnd.Next(length);
        arr[length] = arr[swapWith];
        arr[swapWith] = length;
    }
    var result = RunTest(length, sw, arr, ref idx);
    results.Add(result);
    currentIter++;

    Log($"{result.MemorySizeInBytes} bytes: {result.latency_ns:f2}ns +/- {result.latency_stderr_ns:f4}     ({currentIter * 100.0 / totalIters:f1}% of test run complete)");
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

static LatencyResult RunTest(int length, Stopwatch sw, long[] arr, ref long globalIdx)
{
    var idx = globalIdx;
    var memLatencyNs = MeanVarianceAccumulator.Empty;
    var netMinTestingCount = (int)(minTestingCountPerSize + (Math.Log(maxLong) - Math.Log(length)) * 20);
    var testCount = 0;
    while (testCount < maxTestingCountPerSize
           && (testCount < netMinTestingCount
               || RelativeError(memLatencyNs) >= target_relative_error
               || StdError(memLatencyNs) >= target_absolute_error)) {
        idx = arr[idx]; //avoid prefetching shenanigans
        sw.Restart();
        for (var i = 0; i < innerLoopLength; i++) {
            idx = arr[idx];
        }
        memLatencyNs = memLatencyNs.Add(sw.Elapsed.TotalNanoseconds / innerLoopLength);
        testCount++;
    }
    globalIdx = idx;
    return new(length * sizeof(long), memLatencyNs.Mean, StdError(memLatencyNs), testCount);
}
static double StdError(MeanVarianceAccumulator acc)
    => acc.SampleStandardDeviation / Math.Sqrt(acc.WeightSum);
static double RelativeError(MeanVarianceAccumulator acc)
    => StdError(acc) / acc.Mean;
