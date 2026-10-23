using System.Security.Cryptography;

namespace Reptar;

public static class Ids
{
    private static readonly char[] Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    public static string New(string prefix)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var random = RandomNumberGenerator.GetBytes(8);
        var randomValue = BitConverter.ToUInt64(random, 0) & 0x3FFFFFFFFFF;
        return $"{prefix}_{ToBase32(timestamp, 10)}{ToBase32(randomValue, 10)}";
    }

    private static string ToBase32(ulong value, int length)
    {
        var chars = new char[length];

        for (var index = length - 1; index >= 0; index--)
        {
            chars[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
