using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Reptar;

/// <summary>
/// Small, dependency-free Prometheus metrics registry. Labels are deliberately
/// low-cardinality: method, route template, and status code.
/// </summary>
public sealed class MetricsRegistry
{
    private static readonly double[] Buckets =
        [1, 2, 5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000];

    private readonly ConcurrentDictionary<string, long> _counters =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Histogram> _histograms =
        new(StringComparer.Ordinal);

    public void Record(string method, string route, int status, double durationMs)
    {
        var labels = $"method=\"{Escape(method)}\",route=\"{Escape(route)}\",status=\"{status}\"";
        _counters.AddOrUpdate(
            $"reptar_http_requests_total{{{labels}}}",
            1,
            static (_, value) => value + 1);

        var histogram = _histograms.GetOrAdd(
            $"method=\"{Escape(method)}\",route=\"{Escape(route)}\"",
            static _ => new Histogram(Buckets.Length));

        histogram.Record(durationMs);
    }

    public string Render()
    {
        var builder = new StringBuilder(4_096);

        builder.AppendLine("# HELP reptar_http_requests_total Total HTTP requests.");
        builder.AppendLine("# TYPE reptar_http_requests_total counter");

        foreach (var (key, value) in _counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.AppendLine($"{key} {value.ToString(CultureInfo.InvariantCulture)}");

        builder.AppendLine("# HELP reptar_http_request_duration_ms Request duration in milliseconds.");
        builder.AppendLine("# TYPE reptar_http_request_duration_ms histogram");

        foreach (var (labels, histogram) in _histograms.OrderBy(
            pair => pair.Key,
            StringComparer.Ordinal))
        {
            var snapshot = histogram.Snapshot();

            for (var index = 0; index < Buckets.Length; index++)
            {
                builder.Append("reptar_http_request_duration_ms_bucket{")
                    .Append(labels)
                    .Append(",le=\"")
                    .Append(Buckets[index].ToString(CultureInfo.InvariantCulture))
                    .Append("\"} ")
                    .AppendLine(snapshot.Buckets[index].ToString(CultureInfo.InvariantCulture));
            }

            builder.Append("reptar_http_request_duration_ms_bucket{")
                .Append(labels)
                .Append(",le=\"+Inf\"} ")
                .AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));

            builder.Append("reptar_http_request_duration_ms_sum{")
                .Append(labels)
                .Append("} ")
                .AppendLine(snapshot.Sum.ToString("0.###", CultureInfo.InvariantCulture));

            builder.Append("reptar_http_request_duration_ms_count{")
                .Append(labels)
                .Append("} ")
                .AppendLine(snapshot.Count.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Histogram(int bucketCount)
    {
        private readonly long[] _buckets = new long[bucketCount];
        private readonly object _lock = new();
        private long _count;
        private double _sum;

        public void Record(double value)
        {
            lock (_lock)
            {
                _count++;
                _sum += value;

                for (var index = 0; index < Buckets.Length; index++)
                {
                    if (value <= Buckets[index])
                    {
                        _buckets[index]++;
                        break;
                    }
                }
            }
        }

        public Snapshot Snapshot()
        {
            lock (_lock)
            {
                var cumulative = new long[_buckets.Length];
                var running = 0L;

                for (var index = 0; index < _buckets.Length; index++)
                {
                    running += _buckets[index];
                    cumulative[index] = running;
                }

                return new Snapshot(cumulative, _count, _sum);
            }
        }
    }

    private readonly record struct Snapshot(long[] Buckets, long Count, double Sum);
}

public static class MetricsExtensions
{
    public static ReptarApp UseMetrics(this ReptarApp app) =>
        app.Use(Middlewares.Metrics(app.Metrics));

    public static ReptarApp MapMetrics(
        this ReptarApp app,
        string path = "/metrics",
        ReptarMiddleware? middleware = null)
    {
        Func<Ctx, Task<ReptarResponse>> handler = ctx =>
            Task.FromResult(ctx.Text(
                app.Metrics.Render(),
                200,
                "text/plain; version=0.0.4; charset=utf-8"));

        if (middleware is null)
            app.Get(path, handler);
        else
            app.Get(path, middleware, handler);

        return app;
    }
}
