using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Reptar;

namespace ValidationBench;

/// <summary>
/// Microbenchmark for the <c>Rule&lt;T&gt;</c> typed validation path versus a
/// hand-written validator over the same DTO. Run with:
///
///   dotnet run -c Release --project bench/validation
///
/// The point is to keep the declarative path in the same order of magnitude
/// as manual checks, not to beat it.
/// </summary>
public static class Program
{
    private const int Iterations = 1_000_000;
    private const int WarmupIterations = 100_000;
    private const int Repetitions = 5;

    private static readonly Validator<CreateUserInput> Declarative =
        ReptarApi.Rule<CreateUserInput>()
            .Prop(x => x.Name, rules => rules.Min(2).Max(64))
            .Prop(x => x.Email, rules => rules.Email())
            .Prop(x => x.Age, rules => rules.Min(18).Max(120))
            .Prop(x => x.Bio, rules => rules.Max(500).Optional());

    private static readonly CreateUserInput Valid = new()
    {
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Age = 36,
        Bio = "Mathematician and first programmer.",
    };

    private static readonly CreateUserInput Invalid = new()
    {
        Name = "A",
        Email = "not-an-email",
        Age = 12,
        Bio = new string('x', 600),
    };

    public static int Main(string[] args)
    {
        var jsonPath = args.Length > 0 ? args[0] : null;
        var results = new List<BenchmarkResult>
        {
            Measure("declarative/valid", Valid, input => Declarative.Validate(input).Count == 0),
            Measure("manual/valid", Valid, ManualIsValid),
            Measure("declarative/invalid", Invalid, input => Declarative.Validate(input).Count == 0),
            Measure("manual/invalid", Invalid, ManualIsValid),
        };

        Print(results);

        if (jsonPath is not null)
        {
            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(
                    new
                    {
                        timestamp = DateTimeOffset.UtcNow,
                        runtime = Environment.Version.ToString(),
                        iterations = Iterations,
                        repetitions = Repetitions,
                        results,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    private static BenchmarkResult Measure(
        string name,
        CreateUserInput input,
        Func<CreateUserInput, bool> validate)
    {
        for (var i = 0; i < WarmupIterations; i++)
            validate(input);

        var samples = new List<double>(Repetitions);
        var allocations = new List<long>(Repetitions);

        for (var rep = 0; rep < Repetitions; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();

            var valid = true;
            for (var i = 0; i < Iterations; i++)
                valid &= validate(input);

            var elapsed = Stopwatch.GetTimestamp() - started;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            samples.Add(elapsed * 1_000_000_000.0 / Stopwatch.Frequency / Iterations);
            allocations.Add(allocated / Iterations);

            if (!valid && name.EndsWith("/valid", StringComparison.Ordinal))
                throw new InvalidOperationException($"{name} rejected the valid payload.");
        }

        samples.Sort();
        allocations.Sort();

        return new BenchmarkResult(
            name,
            Math.Round(samples[samples.Count / 2], 2),
            Math.Round(samples[0], 2),
            Math.Round(samples[^1], 2),
            allocations[allocations.Count / 2]);
    }

    private static bool ManualIsValid(CreateUserInput input)
    {
        if (input.Name is null || input.Name.Length is < 2 or > 64)
            return false;

        if (input.Email is null ||
            input.Email.Length < 3 ||
            input.Email.IndexOf('@') <= 0 ||
            input.Email.LastIndexOf('.') <= input.Email.IndexOf('@') + 1)
        {
            return false;
        }

        if (input.Age is < 18 or > 120)
            return false;

        if (input.Bio is not null && input.Bio.Length > 500)
            return false;

        return true;
    }

    private static void Print(IReadOnlyList<BenchmarkResult> results)
    {
        Console.WriteLine(
            "| Case | Median ns/op | Min ns/op | Max ns/op | Allocated B/op |");
        Console.WriteLine("| :--- | ---: | ---: | ---: | ---: |");

        foreach (var result in results)
        {
            Console.WriteLine(
                $"| {result.Name} | {result.MedianNanoseconds.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                $"{result.MinNanoseconds.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                $"{result.MaxNanoseconds.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                $"{result.AllocatedBytes} |");
        }
    }

    public sealed record BenchmarkResult(
        string Name,
        double MedianNanoseconds,
        double MinNanoseconds,
        double MaxNanoseconds,
        long AllocatedBytes);
}

public sealed class CreateUserInput
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Age { get; set; }
    public string? Bio { get; set; }
}
