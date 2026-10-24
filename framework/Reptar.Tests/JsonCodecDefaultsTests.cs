using System.Text.Json;
using Reptar;

namespace Reptar.Tests;

public sealed class JsonCodecDefaultsTests
{
    private sealed class StringOnlyCodec : IJsonCodec
    {
        public string Serialize<T>(T value) => JsonSerializer.Serialize(value);

        public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json);
    }

    [Fact]
    public void Custom_codec_gets_utf8_defaults()
    {
        IJsonCodec codec = new StringOnlyCodec();
        var bytes = codec.SerializeUtf8("hello");
        var value = codec.Deserialize<string>(bytes);

        Assert.Equal("hello", value);
    }
}
