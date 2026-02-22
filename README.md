# C# Console Profiler App

This app launches a target executable and reports:

- **Method usage time** (estimated from .NET CPU sample profiler events)
- **CPU usage** (normalized across logical cores)
- **RAM usage** (peak and average working set)

## Build

```bash
dotnet restore
dotnet build -c Release
```

## Run

```bash
dotnet run -- <path-to-exe> [args...]
```

Examples:

```bash
dotnet run -- ./MyApp.exe --input data.json
```

```bash
dotnet run -- "C:\\Apps\\My App\\MyApp.exe" -- --mode fast
```

## Notes

- Method timing is **sample-based estimation**, not exact instrumentation timing.
- Managed method breakdown requires the target process to be a .NET process.
- Works best for workloads that run at least a few hundred milliseconds.
