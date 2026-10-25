using Reptar;
using static Reptar.ReptarApi;

namespace ReptarGateway;

public static partial class Schema
{
    // High-throughput token telemetry. Rows are written fire-and-forget so
    // each request flows through Reptar's SQLite group-commit channel
    // batcher instead of blocking the response on disk I/O.
    public static readonly TableDef GatewayLogs = DefineTable("gateway_logs",
        Col.Text("id").PrimaryKey(),
        Col.Text("model").NotNull(),
        Col.Int("prompt_tokens"),
        Col.Int("completion_tokens"),
        Col.Real("latency_ms"),
        Col.Text("cached").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );

    public static readonly TableDef[] All = [GatewayLogs];
}
