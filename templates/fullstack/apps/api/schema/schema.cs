namespace {{PROJECT_PASCAL}}.Schema;

using Reptar;
using static Reptar.ReptarApi;

// `partial` lets the source generator emit the ColumnRef companion
// (AppSchema.ItemsCols) into this same class at compile time.
public static partial class AppSchema
{
    // Storage schema lives in TableDefs — plain, attributed-nothing C#.
    // The Reptar source generator emits typed ColumnRef tokens for every
    // table (AppSchema.ItemsCols.Id, .UserId, ...) at compile time.
    public static readonly TableDef Items = DefineTable("items",
        Col.Text("id").PrimaryKey(),
        Col.Text("user_id").NotNull().References("users", "id", onDelete: "cascade"),
        Col.Text("title").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP")
    );
}

// Projection DTOs are clean records. [ReptarEntity] generates a zero-reflection
// parameter mapper so InsertAsync/UpdateAsync hit the fast path.
[ReptarEntity("items")]
public partial record Item(
    string Id,
    string UserId,
    string Title,
    string Content,
    string CreatedAt);

public sealed record CreateItemInput(string Title, string Content);
