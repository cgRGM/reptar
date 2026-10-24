namespace Reptar.Realtime;

public static class TurboStream
{
    public static string Append(string target, string html) =>
        $"<turbo-stream action=\"append\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string Prepend(string target, string html) =>
        $"<turbo-stream action=\"prepend\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string Replace(string target, string html) =>
        $"<turbo-stream action=\"replace\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string Update(string target, string html) =>
        $"<turbo-stream action=\"update\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string Remove(string target) =>
        $"<turbo-stream action=\"remove\" target=\"{target}\"></turbo-stream>";

    public static string Before(string target, string html) =>
        $"<turbo-stream action=\"before\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string After(string target, string html) =>
        $"<turbo-stream action=\"after\" target=\"{target}\"><template>{html}</template></turbo-stream>";

    public static string Morph(string target, string html) =>
        $"<turbo-stream action=\"morph\" target=\"{target}\"><template>{html}</template></turbo-stream>";
}
