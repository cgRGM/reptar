using System.Text;

namespace Reptar;

public sealed class ReptarResponse
{
    public int Status { get; init; } = 200;
    public string? ContentType { get; init; }
    public byte[]? Body { get; init; }
    public string BodyText => Body is not null ? Encoding.UTF8.GetString(Body) : "";
    public Func<CancellationToken, IAsyncEnumerable<byte[]>>? BodyStream { get; init; }
    public Dictionary<string, string> Headers { get; } = new();
    public List<string> SetCookies { get; } = new();

    public static ReptarResponse Json(byte[] utf8Bytes, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json; charset=utf-8",
        Body = utf8Bytes,
    };

    public static ReptarResponse Json(string json, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(json),
    };
}
