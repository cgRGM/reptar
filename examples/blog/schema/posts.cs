using Reptar;
using static Reptar.ReptarApi;

public static partial class Schema
{
    public static readonly TableDef Posts = DefineTable("posts",
        Col.Text("id").PrimaryKey(),
        Col.Text("author_id").NotNull().Indexed(),
        Col.Text("slug").NotNull().Indexed(),
        Col.Text("title").NotNull(),
        Col.Text("content").NotNull(),
        Col.Text("excerpt"),
        Col.Int("published").NotNull().Default(1),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("image_key")
    );
}
