namespace ReptarApp.Schema;

using Reptar;
using static Reptar.ReptarApi;

public static class AppSchema
{
    public static readonly TableDef Items = DefineTable("items",
        Col.Text("id").PrimaryKey(),
        Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade"),
        Col.Text("title").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );
}

public sealed record ItemRecord(
    string Id,
    string UserId,
    string Title,
    string Content,
    string CreatedAt
);

public sealed record CreateItemInput(
    string Title,
    string Content
);
