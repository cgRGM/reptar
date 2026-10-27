using Reptar;

namespace Reptar.Tests;

public sealed class QueryDecodingTests
{
    [Theory]
    [InlineData("subject=hello+world", "subject", "hello world")]
    [InlineData("subject=a%2Bb+c%26d%3De", "subject", "a+b c&d=e")]
    [InlineData("?subject=%E2%98%83+%2F+%3A", "subject", "\u2603 / :")]
    [InlineData("encoded+name=ok", "encoded name", "ok")]
    [InlineData("encoded%2Bname=ok", "encoded+name", "ok")]
    [InlineData("bare+name&other=no", "bare name", "")]
    [InlineData("subject=first&subject=second", "subject", "first")]
    [InlineData("subject=one=two", "subject", "one=two")]
    [InlineData("subject=%252B", "subject", "%2B")]
    [InlineData("different=value", "missing", null)]
    public void Query_components_follow_URLSearchParams_form_encoding(string query, string key, string? expected)
    {
        var request = new ReptarRequest { Method = "GET", Path = "/", Query = query };
        Assert.Equal(expected, request.QueryValue(key));
    }
}
