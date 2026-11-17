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

    /// <summary>
    /// Сумма в валюте аккаунта: «$0,92», «¥12,30», «EUR 3,00».
    /// Два знака — для шапки и коротких подписей; полное значение — <see cref="MoneyExact"/>.
    /// </summary>
    public static string Money(decimal value, string? currency) => Compose(value, "0.00", currency);

    /// <summary>Сумма без округления до копеек — для tooltip (минимум 2 знака, до 9).</summary>
    public static string MoneyExact(decimal value, string? currency) => Compose(value, "0.00#######", currency);

    /// <summary>Символ валюты: $ для USD, ¥ для CNY/RMB, иначе код валюты с пробелом.</summary>
    public static string Sign(string? currency)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        return code switch
        {
            "" => "",
            "USD" or "$" => "$",
            "CNY" or "RMB" or "YUAN" or "¥" => "¥",
            "EUR" or "€" => "€",
            _ => code + " ",
        };
    }

    static string Compose(decimal value, string format, string? currency)
    {
        var symbol = Sign(currency);
        return value < 0 ? "-" + symbol + Number(-value, format) : symbol + Number(value, format);
    }

    static string Number(decimal value, string format) =>
        value.ToString(format, Inv).Replace('.', ',');


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
