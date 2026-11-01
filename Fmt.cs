using System.Globalization;

namespace TokenMonitor;

public static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Tokens(long value)
    {
        if (value < 0) value = 0;
        if (value < 1_000) return value.ToString(Inv);
        if (value < 10_000) return (value / 1_000.0).ToString("0.0", Inv) + "k";
        if (value < 1_000_000) return Math.Round(value / 1_000.0).ToString("0", Inv) + "k";
        if (value < 1_000_000_000) return (value / 1_000_000.0).ToString("0.00", Inv) + "M";
        return (value / 1_000_000_000.0).ToString("0.00", Inv) + "B";
    }

    public static string Exact(long value) => value.ToString("N0", Inv);

    public static string Duration(long ms)
    {
        if (ms <= 0) return "—";
        var seconds = ms / 1000.0;
        if (seconds < 1) return "<1s";
        if (seconds < 60) return seconds.ToString("0.#", Inv) + "s";
        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds - minutes * 60);
        if (rest == 60) { minutes++; rest = 0; }
        if (minutes < 60) return $"{minutes}m{rest:00}s";
        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours}h{minutes:00}m";
    }

    public static string Preview(string text, int max = 90)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    public static string Folder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.TrimEnd('/', '\\');
        var name = trimmed[(trimmed.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        return name.Length == 0 ? trimmed : name;
    }
}
