using Reptar;

public static class SwitchyardVerification
{
    public static async Task Run(IDbClient db)
    {
        // Values are also checked by an independent JavaScript implementation.
        var subjects = new[] { "customer-42", "vip-α", "a:b/c", "🚂", "" };
        var expected = new[] { 50, 67, 24, 95, 82 };
        for (var index = 0; index < subjects.Length; index++)
        {
            var actual = Rollouts.Bucket("project", "production", "checkout", subjects[index]);
            if (actual != expected[index]) throw new InvalidOperationException("Rollout golden vector mismatch.");
            Console.WriteLine($"bucket:{subjects[index]}:{actual}");
        }
        if (Rollouts.Bucket("a", "bc", "d", "e") == Rollouts.Bucket("ab", "c", "d", "e"))
            throw new InvalidOperationException("Length-prefix collision in test vector.");

        var project = Ids.New("rollback");
        await db.InsertAsync(SwitchyardSchema.Projects, new ProjectRow(project, "test-owner", "Rollback probe"));
        await db.ExecuteAsync("INSERT INTO sy_flags VALUES (@project, 'production', 'probe', 1, 50, '[]', 1, 0)", ("project", project));
        var audit = new AuditRow(Ids.New("audit"), project, "production", "probe", 0, "test-owner", "test-time");
        await db.InsertAsync(SwitchyardSchema.Audit, audit);
        var failed = false;
        try
        {
            await db.TransactionAsync(async tx =>
            {
                await tx.InsertAsync(SwitchyardSchema.Versions, new VersionRow(project, "production", "probe", 1, 1, 50, "[]", "test-owner", "test-time"));
                await tx.ExecuteAsync("UPDATE sy_flags SET version=1 WHERE project_id=@project", ("project", project));
                // Force failure on the final write, after version and active pointer changed.
                await tx.InsertAsync(SwitchyardSchema.Audit, audit);
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
        if (!failed) throw new InvalidOperationException("Expected duplicate audit write to fail.");
        var flag = await db.QuerySingleOrDefaultAsync<FlagRow>("SELECT * FROM sy_flags WHERE project_id=@project", ("project", project));
        var versions = await db.QueryAsync<VersionRow>("SELECT * FROM sy_versions WHERE project_id=@project", ("project", project));
        var audits = await db.QueryAsync<AuditRow>("SELECT * FROM sy_audit WHERE project_id=@project", ("project", project));
        if (flag?.Version != 0 || versions.Count != 0 || audits.Count != 1)
            throw new InvalidOperationException("Transaction leaked a partial publish.");
        Console.WriteLine("rollback:PASS");
    }
}
