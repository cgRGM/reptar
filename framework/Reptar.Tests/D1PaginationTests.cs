using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed class D1PaginationTests
{
    [Theory]
    [InlineData("[]", 10, 0, "[10]", "select * from posts limit ?")]
    [InlineData("[ ]", 20, 10, "[ 20,10]", "select * from posts limit ? offset ?")]
    [InlineData("[\"active\",7]", 20, 10, "[\"active\",7,20,10]", "select * from posts limit ? offset ?")]
    [InlineData("[]", 0, 10, "[]", "select * from posts")]
    public void Pagination_preserves_bind_order_and_never_interpolates_page_values(
        string parameters, int limit, int offset, string expectedParameters, string expectedSql)
    {
        var page = SqlPagination.AppendPositionalJson("select * from posts", parameters, limit, offset);
        Assert.Equal(expectedSql, page.Sql);
        Assert.Equal(expectedParameters, page.ParamsJson);
        using var document = JsonDocument.Parse(page.ParamsJson);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }

    [Fact]
    public void Different_pages_share_one_statement_template()
    {
        var first = SqlPagination.AppendPositionalJson("select * from posts", "[]", 10, 5);
        var second = SqlPagination.AppendPositionalJson("select * from posts", "[]", 25, 40);
        Assert.Equal(first.Sql, second.Sql);
        Assert.NotEqual(first.ParamsJson, second.ParamsJson);
        Assert.Throws<ArgumentException>(() => SqlPagination.AppendPositionalJson("select 1", "{}", 10, 0));
    }
}
