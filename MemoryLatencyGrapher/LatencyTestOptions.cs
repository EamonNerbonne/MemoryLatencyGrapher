using System.Diagnostics;
using System.Text.Json;
using VectSharp.Plots;
using VectSharp.SVG;

namespace MemoryLatencyGrapher;

public sealed record LatencyTestOptions
{
    public int OuterLoopLength { get; init; } = 20; //how many times to run the inner loop before trusting the timings
    public int MemorySizeGranularity { get; init; } = 2; //how many times to run the inner loop before trusting the timings
    public long MaxMemoryInBytes { get; init; } = 1L << 31;
    public bool LogProgressToConsole { get; init; } = true;
    public bool LogProgressToFile { get; init; } = true;
    public bool GenerateLatencyPlotSvg { get; init; } = true;
    public bool SaveLatencyStatsToJson { get; init; } = true;

    public void SaveToSvg(LatencyResult[] latencyResults, string svgFileName)
    {
        if (!GenerateLatencyPlotSvg) {
            return;
        }
        var Ymax = latencyResults.Select(r => r.Y_plus2stderr).Max();
        var Ymin = Math.Max(0.1, latencyResults.Select(r => r.Y_min2stderr).Min());
        var Xmax = latencyResults.Select(r => r.X).Max();
        var Xmin = latencyResults.Select(r => r.X).Min();
        //var system = new LinLogCoordinateSystem2D(Xmin, Xmax, Ymin, Ymax, 1800, 1000);
        //var system = new LinLogCoordinateSystem2D(Xmin, Xmax, 0.0, Ymax, 1800, 1000);
        var system = new LogarithmicCoordinateSystem2D(Xmin, Xmax, Ymin, Ymax, 1800, 1000);

        var renderedPlot = Plot.Create.LineCharts(
            [
                latencyResults.Select(r => (r.X, r.Y)).ToArray(),
                latencyResults.Select(r => (r.X, r.Y_min2stderr)).ToArray(),
                latencyResults.Select(r => (r.X, r.Y_plus2stderr)).ToArray(),
            ],
            width: 1800,
            height: 1000,
            xAxisTitle: "bytes",
            yAxisTitle: "latency (ps)",
            coordinateSystem: system
        ).Render();
        renderedPlot.SaveAsSVG(svgFileName);

        if (OperatingSystem.IsWindows()) {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + svgFileName + "\"" });
        } else if (OperatingSystem.IsMacOS()) {
            Process.Start(new ProcessStartInfo("open") { Arguments = $"\"{svgFileName}\"" });
        } else if (OperatingSystem.IsLinux()) {
            Process.Start(new ProcessStartInfo("xdg-open") { Arguments = $"\"{svgFileName}\"" });
        }
    }

    public void SaveResultsToJson(LatencyResult[] latencyResults, string jsonFileName)
    {
        if (SaveLatencyStatsToJson) {
            using var jsonStream = File.OpenWrite(jsonFileName);
            JsonSerializer.Serialize(jsonStream, latencyResults, ResultJsonSerializerContext.Default.LatencyResultArray);
        }
    }
    public static LatencyTestOptions ParseOptions()
    {
        var args = Environment.GetCommandLineArgs();
        var options = new LatencyTestOptions();
        if (args.Length > 1) {
            using var jsonStream = File.OpenRead(args[1]);
            options = JsonSerializer.Deserialize(jsonStream, ResultJsonSerializerContext.Default.LatencyTestOptions) ?? options;
        }
        return options;
    }
}
