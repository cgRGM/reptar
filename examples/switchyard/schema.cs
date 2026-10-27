using Reptar;
using static Reptar.ReptarApi;

public static class SwitchyardSchema
{
    public static readonly TableDef Projects = DefineTable("sy_projects",
        Col.Text("id").PrimaryKey(), Col.Text("owner_id").NotNull().Indexed(), Col.Text("name").NotNull());
    public static readonly TableDef Flags = DefineTable("sy_flags",
        Col.Text("project_id").NotNull().References(Projects), Col.Text("environment").NotNull(),
        Col.Text("key").NotNull(), Col.Int("enabled").NotNull(), Col.Int("rollout").NotNull(),
        Col.Text("targets").NotNull(), Col.Int("revision").NotNull(), Col.Int("version").NotNull())
        .PrimaryKey("project_id", "environment", "key");
    public static readonly TableDef Versions = DefineTable("sy_versions",
        Col.Text("project_id").NotNull().References(Projects), Col.Text("environment").NotNull(),
        Col.Text("key").NotNull(), Col.Int("version").NotNull(), Col.Int("enabled").NotNull(),
        Col.Int("rollout").NotNull(), Col.Text("targets").NotNull(), Col.Text("actor_id").NotNull(),
        Col.Text("created_at").NotNull()).PrimaryKey("project_id", "environment", "key", "version");
    public static readonly TableDef Audit = DefineTable("sy_audit",
        Col.Text("id").PrimaryKey(), Col.Text("project_id").NotNull().References(Projects),
        Col.Text("environment").NotNull(), Col.Text("key").NotNull(), Col.Int("version").NotNull(),
        Col.Text("actor_id").NotNull(), Col.Text("created_at").NotNull());
    public static readonly TableDef[] All = [Projects, Flags, Versions, Audit];

    static SwitchyardSchema()
    {
        EntityMapper<ProjectRow>.Factory = _ => row => new ProjectRow(
            (string)row["id"], (string)row["owner_id"], (string)row["name"]);
        EntityMapper<FlagRow>.Factory = _ => row => new FlagRow(
            (string)row["project_id"], (string)row["environment"], (string)row["key"],
            Convert.ToInt32(row["enabled"]), Convert.ToInt32(row["rollout"]), (string)row["targets"],
            Convert.ToInt32(row["revision"]), Convert.ToInt32(row["version"]));
        EntityMapper<VersionRow>.Factory = _ => row => new VersionRow(
            (string)row["project_id"], (string)row["environment"], (string)row["key"],
            Convert.ToInt32(row["version"]), Convert.ToInt32(row["enabled"]), Convert.ToInt32(row["rollout"]),
            (string)row["targets"], (string)row["actor_id"], (string)row["created_at"]);
        EntityMapper<AuditRow>.Factory = _ => row => new AuditRow(
            (string)row["id"], (string)row["project_id"], (string)row["environment"], (string)row["key"],
            Convert.ToInt32(row["version"]), (string)row["actor_id"], (string)row["created_at"]);
    }
}

public sealed record ProjectRow(string Id, string OwnerId, string Name) : IReptarEntity
{
    public (string Name, object? Value)[] ToDbParameters() => [("id", Id), ("owner_id", OwnerId), ("name", Name)];
}
public sealed record FlagRow(string ProjectId, string Environment, string Key, int Enabled, int Rollout,
    string Targets, int Revision, int Version);
public sealed record VersionRow(string ProjectId, string Environment, string Key, int Version,
    int Enabled, int Rollout, string Targets, string ActorId, string CreatedAt) : IReptarEntity
{
    public (string Name, object? Value)[] ToDbParameters() => [
        ("project_id", ProjectId), ("environment", Environment), ("key", Key), ("version", Version),
        ("enabled", Enabled), ("rollout", Rollout), ("targets", Targets), ("actor_id", ActorId), ("created_at", CreatedAt)];
}
public sealed record AuditRow(string Id, string ProjectId, string Environment, string Key, int Version,
    string ActorId, string CreatedAt) : IReptarEntity
{
    public (string Name, object? Value)[] ToDbParameters() => [
        ("id", Id), ("project_id", ProjectId), ("environment", Environment), ("key", Key),
        ("version", Version), ("actor_id", ActorId), ("created_at", CreatedAt)];
}
public sealed record CreateProject(string Name);
public sealed record CreateFlag(string Key, string Environment);
public sealed record DraftInput(bool Enabled, int Rollout, string[] Targets, int ExpectedRevision);
public sealed record PublishInput(int ExpectedRevision, int ExpectedVersion);
public sealed record EvaluateInput(string Subject);
public sealed record Evaluation(bool Enabled, int Bucket, int Version, string Reason);
public sealed record Health(string Status, string Target);
public sealed record Problem(string Code, string Message);

public static class Rollouts
{
    // FNV-1a over UTF-8 with length-prefixed fields: stable across runtimes and
    // unambiguous even when subject keys contain punctuation or Unicode.
    public static int Bucket(string project, string environment, string key, string subject)
    {
        uint hash = 2166136261;
        foreach (var part in new[] { project, environment, key, subject })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(part);
            uint length = (uint)bytes.Length;
            for (var shift = 0; shift < 32; shift += 8)
                hash = unchecked((hash ^ (byte)(length >> shift)) * 16777619);
            foreach (var value in bytes)
                hash = unchecked((hash ^ value) * 16777619);
        }
        return (int)(hash % 100);
    }
}
