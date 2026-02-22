using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Stacks;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var splitIndex = Array.IndexOf(args, "--");
string exePath;
string[] targetArgs;

if (splitIndex >= 0)
{
    exePath = string.Join(' ', args[..splitIndex]).Trim();
    targetArgs = args[(splitIndex + 1)..];
}
else
{
    exePath = args[0];
    targetArgs = args.Skip(1).ToArray();
}

if (string.IsNullOrWhiteSpace(exePath))
{
    Console.WriteLine("Executable path cannot be empty.");
    PrintUsage();
    return;
}

if (!File.Exists(exePath))
{
    Console.WriteLine($"Executable not found: {exePath}");
    return;
}

var processStartInfo = new ProcessStartInfo
{
    FileName = exePath,
    Arguments = string.Join(' ', targetArgs.Select(EscapeArg)),
    UseShellExecute = false,
    RedirectStandardOutput = false,
    RedirectStandardError = false,
};

using var process = Process.Start(processStartInfo);
if (process is null)
{
    Console.WriteLine("Failed to start process.");
    return;
}

Console.WriteLine($"Started PID {process.Id}: {exePath} {processStartInfo.Arguments}");

var sampleFile = Path.Combine(Path.GetTempPath(), $"profiler-{process.Id}-{Guid.NewGuid():N}.nettrace");
var monitorTask = MonitorMemoryAsync(process);

var methodSamples = new Dictionary<string, long>(StringComparer.Ordinal);
var eventPipeProviders = new List<EventPipeProvider>
{
    new("Microsoft-DotNETCore-SampleProfiler", System.Diagnostics.Tracing.EventLevel.Informational),
};

var stopwatch = Stopwatch.StartNew();
TimeSpan cpuStart = process.TotalProcessorTime;

using (var client = new DiagnosticsClient(process.Id))
using (var session = client.StartEventPipeSession(eventPipeProviders, requestRundown: true))
using (var fs = File.Create(sampleFile))
{
    var copyTask = session.EventStream.CopyToAsync(fs);

    process.WaitForExit();
    session.Stop();

    await copyTask;
}

stopwatch.Stop();
await monitorTask;
TimeSpan cpuEnd = process.TotalProcessorTime;

var elapsed = stopwatch.Elapsed;
var cpuUsage = CalculateCpuUsage(cpuStart, cpuEnd, elapsed);

try
{
    ParseMethodSamples(sampleFile, methodSamples);
}
catch (Exception ex)
{
    Console.WriteLine($"Warning: Unable to parse method samples ({ex.Message}).");
}
finally
{
    TryDelete(sampleFile);
    TryDelete(sampleFile + ".etlx");
}

Console.WriteLine();
Console.WriteLine("========== Profile Summary ==========");
Console.WriteLine($"Process Exit Code : {process.ExitCode}");
Console.WriteLine($"Elapsed Time      : {elapsed.TotalMilliseconds:F2} ms");
Console.WriteLine($"CPU Usage         : {cpuUsage:F2}% (normalized by {Environment.ProcessorCount} cores)");
Console.WriteLine($"Peak RAM          : {FormatBytes(monitorTask.Result.PeakWorkingSetBytes)}");
Console.WriteLine($"Avg RAM           : {FormatBytes(monitorTask.Result.AverageWorkingSetBytes)}");
Console.WriteLine();
Console.WriteLine("Top Methods by Estimated CPU Time (sample-based)");

if (methodSamples.Count == 0)
{
    Console.WriteLine("No managed method samples captured.");
}
else
{
    var totalSamples = methodSamples.Values.Sum();
    var msPerSample = totalSamples == 0 ? 0 : elapsed.TotalMilliseconds / totalSamples;

    foreach (var entry in methodSamples.OrderByDescending(x => x.Value).Take(20))
    {
        var estimatedMs = entry.Value * msPerSample;
        var pct = totalSamples == 0 ? 0 : (entry.Value * 100.0 / totalSamples);
        Console.WriteLine($"{pct,6:F2}% | {estimatedMs,10:F2} ms | {entry.Key}");
    }
}

static void ParseMethodSamples(string nettracePath, Dictionary<string, long> methodSamples)
{
    var etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);
    using var traceLog = new TraceLog(etlxPath);

    var source = traceLog.Events.GetSource();
    var stackSource = new MutableTraceEventStackSource(traceLog);
    _ = new SampleProfilerThreadTimeComputer(source, stackSource);

    source.Process();

    var samples = new StackSourceSample();
    for (var sampleIndex = stackSource.GetFirstSampleIndex(); sampleIndex != StackSourceSampleIndex.Invalid; sampleIndex = stackSource.GetNextSampleIndex(sampleIndex))
    {
        stackSource.GetSampleByIndex(sampleIndex, samples);
        if (samples.StackIndex == StackSourceCallStackIndex.Invalid)
        {
            continue;
        }

        var frameIndex = stackSource.GetFrameIndex(samples.StackIndex);
        var methodName = stackSource.GetFrameName(frameIndex, false);

        if (string.IsNullOrWhiteSpace(methodName) || methodName.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        methodSamples.TryGetValue(methodName, out var current);
        methodSamples[methodName] = current + 1;
    }
}

static async Task<MemoryStats> MonitorMemoryAsync(Process process)
{
    long peak = 0;
    long total = 0;
    long samples = 0;

    while (!process.HasExited)
    {
        process.Refresh();
        var ws = process.WorkingSet64;
        peak = Math.Max(peak, ws);
        total += ws;
        samples++;
        await Task.Delay(100);
    }

    process.Refresh();
    var finalWs = process.WorkingSet64;
    peak = Math.Max(peak, finalWs);
    total += finalWs;
    samples++;

    return new MemoryStats(peak, samples == 0 ? 0 : total / samples);
}

static string EscapeArg(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return "\"\"";
    }

    if (!value.Contains(' ') && !value.Contains('"'))
    {
        return value;
    }

    return "\"" + value.Replace("\"", "\\\"") + "\"";
}

static double CalculateCpuUsage(TimeSpan cpuStart, TimeSpan cpuEnd, TimeSpan elapsed)
{
    if (elapsed.TotalMilliseconds <= 0)
    {
        return 0;
    }

    var cpuMs = (cpuEnd - cpuStart).TotalMilliseconds;
    return cpuMs / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
}

static string FormatBytes(long bytes)
{
    string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
    double value = bytes;
    var index = 0;

    while (value >= 1024 && index < suffixes.Length - 1)
    {
        value /= 1024;
        index++;
    }

    return value.ToString("F2", CultureInfo.InvariantCulture) + " " + suffixes[index];
}

static void TryDelete(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    try
    {
        File.Delete(path);
    }
    catch
    {
        // Ignore cleanup failures.
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- <path-to-exe> [arg1 arg2 ...]");
    Console.WriteLine("  dotnet run -- \"C:\\Path To\\app.exe\" -- arg1 arg2");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("- Method usage time is estimated from managed CPU samples (SampleProfiler).");
    Console.WriteLine("- CPU usage is normalized across all logical processors.");
    Console.WriteLine("- RAM usage reports peak and average working set while the process runs.");
}

readonly record struct MemoryStats(long PeakWorkingSetBytes, long AverageWorkingSetBytes);
