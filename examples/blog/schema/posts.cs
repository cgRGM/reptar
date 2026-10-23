public static partial class Schema
{
    public static readonly TableDef Posts = DefineTable("posts", new
    {
        Id = Col.Text("id").PrimaryKey(),
        AuthorId = Col.Text("author_id").NotNull().References("users", "id", onDelete: "cascade").Indexed(),
        Slug = Col.Text("slug").NotNull().Unique(),
        Title = Col.Text("title").NotNull(),
        Content = Col.Text("content").NotNull(),
        Excerpt = Col.Text("excerpt"),
        Published = Col.Int("published").NotNull().Default(0),
        CreatedAt = Col.Text("created_at").DefaultSql("CURRENT_TIMESTAMP"),
        UpdatedAt = Col.Text("updated_at").DefaultSql("CURRENT_TIMESTAMP"),
    });
}
