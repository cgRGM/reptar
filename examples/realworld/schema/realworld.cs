using Reptar;
using static Reptar.ReptarApi;

public static class RealWorldSchema
{
    public static readonly TableDef Users = DefineTable("rw_users",
        Col.Text("id").PrimaryKey(),
        Col.Text("username").NotNull().Unique(),
        Col.Text("email").NotNull().Unique(),
        Col.Text("password_hash").NotNull(),
        Col.Text("bio"),
        Col.Text("image"),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Articles = DefineTable("rw_articles",
        Col.Text("id").PrimaryKey(),
        Col.Text("author_id").NotNull().Indexed(),
        Col.Text("slug").NotNull().Unique(),
        Col.Text("title").NotNull(),
        Col.Text("description").NotNull(),
        Col.Text("body").NotNull(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Tags = DefineTable("rw_tags",
        Col.Text("id").PrimaryKey(),
        Col.Text("name").NotNull().Unique());

    public static readonly TableDef ArticleTags = DefineTable("rw_article_tags",
        Col.Text("id").PrimaryKey(),
        Col.Text("article_id").NotNull().Indexed(),
        Col.Text("tag_id").NotNull().Indexed());

    public static readonly TableDef Comments = DefineTable("rw_comments",
        Col.Text("id").PrimaryKey(),
        Col.Text("article_id").NotNull().Indexed(),
        Col.Text("author_id").NotNull().Indexed(),
        Col.Text("body").NotNull(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"),
        Col.Text("updated_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Favorites = DefineTable("rw_favorites",
        Col.Text("id").PrimaryKey(),
        Col.Text("article_id").NotNull().Indexed(),
        Col.Text("user_id").NotNull().Indexed(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef Follows = DefineTable("rw_follows",
        Col.Text("id").PrimaryKey(),
        Col.Text("follower_id").NotNull().Indexed(),
        Col.Text("followed_id").NotNull().Indexed(),
        Col.Text("created_at").NotNull().DefaultSql("CURRENT_TIMESTAMP"));

    public static readonly TableDef[] All =
    [
        Users,
        Articles,
        Tags,
        ArticleTags,
        Comments,
        Favorites,
        Follows,
    ];
}
