using Reptar;
using static Reptar.ReptarApi;

public static partial class Schema
{
    public static readonly TableDef Sessions = DefineTable("agent_sessions",
        Col.Text("id").PrimaryKey(),
        Col.Text("title").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Messages = DefineTable("agent_messages",
        Col.Text("id").PrimaryKey(),
        Col.Text("session_id").NotNull().Indexed(),
        Col.Text("role").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("tool_calls"),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef[] All = [Sessions!, Messages!, .. SessionTreeSchema.All];
}
