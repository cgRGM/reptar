using System.Text.Json;

namespace Reptar;

public interface IJsonCodec
{
    string Serialize<T>(T value);
    T? Deserialize<T>(string json);
}

public sealed class DefaultJsonCodec : IJsonCodec
{
    public string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ReptarJson.Options);

    public T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ReptarJson.Options);
}
