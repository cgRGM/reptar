using System.Runtime.InteropServices.JavaScript;

namespace Reptar;

public sealed class WasmCrypto : ICrypto
{
    [JSImport("hashPassword", "app.host")]
    private static extern Task<string> HashPasswordJs(string password);

    [JSImport("verifyPassword", "app.host")]
    private static extern Task<bool> VerifyPasswordJs(
        string password,
        string hash);

    [JSImport("sha256", "app.host")]
    private static extern Task<string> Sha256Js(string input);

    [JSImport("randomToken", "app.host")]
    private static extern string RandomTokenJs(int bytes);

    public Task<string> HashPassword(string password) =>
        HashPasswordJs(password);

    public Task<bool> VerifyPassword(string password, string hash) =>
        VerifyPasswordJs(password, hash);

    public Task<string> Sha256(string input) => Sha256Js(input);

    public string RandomToken(int bytes = 32) => RandomTokenJs(bytes);
}
