using System;
using Xunit;

namespace EmbeddingSearchViewer.Tests.Benchmarks;

/// <summary>
/// A <see cref="FactAttribute"/> that only runs when the <c>ESV_RUN_BENCHMARKS</c> environment variable is set to
/// <c>1</c>. This keeps the large-scale (120k x 768) latency benchmark out of the default <c>dotnet test</c> run, which
/// must stay fast, while still allowing it to be run explicitly (see docs/OPERATIONS.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ESV_RUN_BENCHMARKS";

    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Large-scale benchmark is opt-in: set {EnvironmentVariable}=1 to run it (see docs/OPERATIONS.md).";
        }
    }
}
