using System.IO;

namespace TokenMonitor;

public enum DeepSeekCredentialStatus
{
    Ok,
    NoFile,
    NoGrant,
    Ambiguous,
    Malformed,
    AccessDenied,
}

/// <summary>Результат поиска platform-grant'а DSH. Токен живёт только в памяти.</summary>
public sealed record DeepSeekCredential(
    DeepSeekCredentialStatus Status,
    string? Token,
    string? Issuer,
    string Message)
{
    public bool HasToken => Status == DeepSeekCredentialStatus.Ok && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Чтение grant'а DeepSeek-аккаунта из <c>%DSH_HOME%\.credentials.yaml</c> (TZ §3.1).
/// Формат — внутренний для DSH, поэтому парсер защитный: разбирает только известную
/// структуру (version / records / kind / payload), а на неожиданном вводе возвращает
/// понятный статус, а не исключение. Токен никогда не логируется.
/// </summary>
public static class DeepSeekCredentials
{
    public const string DefaultGrantId = "deepseek-account-platform/default";
    const string PlatformGrantPrefix = "deepseek-account-platform/";

    /// <summary>
    /// Ищет токен: env-переменная (приоритет) → файл credentials.
    /// </summary>
    public static DeepSeekCredential Read(DeepSeekAccountSettings settings)
    {
        var origin = DeepSeekAccountSettings.NormalizeOrigin(settings.PlatformOrigin);

        var envName = settings.TokenEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(envName))
        {
            var fromEnv = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return new DeepSeekCredential(
                    DeepSeekCredentialStatus.Ok,
                    fromEnv.Trim(),
                    origin,
                    $"токен из переменной окружения {envName}");
            }
        }

        return ReadFile(settings.ResolveCredentialsPath(), origin);
    }

    /// <summary>Разбор файла credentials. <paramref name="origin"/> — ожидаемый issuer.</summary>
    public static DeepSeekCredential ReadFile(string path, string origin)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DeepSeekCredential(DeepSeekCredentialStatus.NoFile, null, null,
                "не задан путь к credentials DSH");
        }

        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new DeepSeekCredential(DeepSeekCredentialStatus.NoFile, null, null,
                    $"нет файла {path}");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (UnauthorizedAccessException)
        {
            return new DeepSeekCredential(DeepSeekCredentialStatus.AccessDenied, null, null,
                "нет доступа к credentials DSH");
        }
        catch (IOException error)
        {
            return new DeepSeekCredential(DeepSeekCredentialStatus.AccessDenied, null, null,
                $"credentials DSH недоступны: {error.Message}");
        }

        return FromYaml(text, origin);
    }

    /// <summary>Чистая функция выбора grant'а из текста YAML (для тестов/фикстур).</summary>
    public static DeepSeekCredential FromYaml(string yaml, string origin)
    {
        var records = ParseRecords(yaml, out var malformed);
        if (records.Count == 0)
        {
            return malformed
                ? new DeepSeekCredential(DeepSeekCredentialStatus.Malformed, null, null,
                    "структура credentials DSH не распознана")
                : new DeepSeekCredential(DeepSeekCredentialStatus.NoGrant, null, null,
                    "в credentials DSH нет grant'а DeepSeek-аккаунта");
        }

        // 1. Приоритет — запись deepseek-account-platform/default.
        if (records.TryGetValue(DefaultGrantId, out var preferred))
        {
            var result = Validate(preferred, origin);
            if (result.Status != DeepSeekCredentialStatus.NoGrant) return result;
        }

        // 2. Иначе — единственная запись с токеном и совпадающим issuer.
        var candidates = new List<KeyValuePair<string, YamlRecord>>();
        foreach (var pair in records)
        {
            if (!pair.Key.StartsWith(PlatformGrantPrefix, StringComparison.Ordinal)) continue;

            var token = pair.Value.Get("token");
            if (string.IsNullOrWhiteSpace(token)) continue;

            var issuer = pair.Value.Get("issuer");
            if (!IssuerMatches(issuer, origin)) continue;

            candidates.Add(pair);
        }

        if (candidates.Count == 1) return Ok(candidates[0].Value);

        if (candidates.Count > 1)
        {
            // Несколько платформенных grant'ов: берём default, если он попал в список,
            // иначе — не угадываем.
            var fallback = candidates.FirstOrDefault(c => c.Key == DefaultGrantId);
            return fallback.Value is not null
                ? Ok(fallback.Value)
                : new DeepSeekCredential(DeepSeekCredentialStatus.Ambiguous, null, null,
                    $"несколько grant'ов DeepSeek-аккаунта (например, {candidates[0].Key})");
        }

        // Есть записи, но без token/issuer — считаем, что вход не выполнен.
        return new DeepSeekCredential(DeepSeekCredentialStatus.NoGrant, null, null,
            "grant DeepSeek-аккаунта без токена или с другим issuer");
    }

    static DeepSeekCredential Validate(YamlRecord record, string origin)
    {
        var token = record.Get("token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DeepSeekCredential(DeepSeekCredentialStatus.NoGrant, null, null,
                "grant DeepSeek-аккаунта без токена");
        }

        var issuer = record.Get("issuer");
        if (!string.IsNullOrWhiteSpace(issuer) && !IssuerMatches(issuer, origin))
        {
            return new DeepSeekCredential(DeepSeekCredentialStatus.NoGrant, null, null,
                $"grant DeepSeek-аккаунта выдан для {issuer}, а настроен {origin}");
        }

        return Ok(record);
    }

    static DeepSeekCredential Ok(YamlRecord record) =>
        new(DeepSeekCredentialStatus.Ok, record.Get("token"), record.Get("issuer"),
            "grant DeepSeek-аккаунта из DSH");

    static bool IssuerMatches(string? issuer, string origin)
    {
        if (string.IsNullOrWhiteSpace(issuer)) return true;

        var left = Normalize(issuer);
        var right = Normalize(origin);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    static string Normalize(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : trimmed;
    }

    sealed class YamlRecord
    {
        readonly Dictionary<string, string> payload = new(StringComparer.OrdinalIgnoreCase);

        public string Id { get; }
        public string Kind { get; private set; } = string.Empty;

        public YamlRecord(string id) => Id = id;

        public void SetKind(string kind) => Kind = kind;
        public void Set(string key, string value) => payload[key] = value;
        public string? Get(string key) => payload.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Минимальный YAML-разбор под известную структуру DSH:
    /// <code>
    /// records:
    ///   &lt;id&gt;:
    ///     kind: grant
    ///     payload:
    ///       token: &lt;...&gt;
    /// </code>
    /// Поддерживаются отступы пробелами, кавычки, комментарии. Всё остальное — malformed.
    /// </summary>
    static Dictionary<string, YamlRecord> ParseRecords(string yaml, out bool malformed)
    {
        malformed = false;
        var records = new Dictionary<string, YamlRecord>(StringComparer.Ordinal);

        YamlRecord? current = null;
        var inRecords = false;
        var inPayload = false;
        var recordIndent = -1;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var trimmed = line.TrimStart(' ');
            if (trimmed.StartsWith('#')) continue;

            var indent = line.Length - trimmed.Length;
            if (trimmed.Length == 0) continue;

            if (indent == 0)
            {
                inRecords = trimmed.Equals("records:", StringComparison.Ordinal);
                current = null;
                inPayload = false;
                continue;
            }

            if (!inRecords) continue;

            if (current is null)
            {
                if (indent <= 0 || !TrySplitKey(trimmed, out var id, out var value))
                {
                    malformed = true;
                    continue;
                }

                if (value.Length > 0) { malformed = true; continue; }

                current = new YamlRecord(Unquote(id));
                records[current.Id] = current;
                recordIndent = indent;
                inPayload = false;
                continue;
            }

            if (indent <= recordIndent)
            {
                // Следующая запись на том же уровне.
                if (!TrySplitKey(trimmed, out var id, out var value) || value.Length > 0)
                {
                    malformed = true;
                    current = null;
                    inPayload = false;
                    continue;
                }

                current = new YamlRecord(Unquote(id));
                records[current.Id] = current;
                recordIndent = indent;
                inPayload = false;
                continue;
            }

            if (indent == recordIndent + 2)
            {
                if (!TrySplitKey(trimmed, out var key, out var value))
                {
                    malformed = true;
                    continue;
                }

                if (key.Equals("kind", StringComparison.OrdinalIgnoreCase))
                {
                    current.SetKind(Unquote(value));
                    inPayload = false;
                }
                else if (key.Equals("payload", StringComparison.OrdinalIgnoreCase))
                {
                    inPayload = value.Length == 0;
                }
                else
                {
                    inPayload = false;
                }

                continue;
            }

            if (inPayload && TrySplitKey(trimmed, out var payloadKey, out var payloadValue))
            {
                current.Set(Unquote(payloadKey), Unquote(payloadValue));
            }
        }

        return records;
    }

    static bool TrySplitKey(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var index = line.IndexOf(':');
        if (index <= 0) return false;

        key = line[..index];
        value = line[(index + 1)..].Trim();

        // «key:» без значения — тоже валидный ключ (records, payload).
        return !key.Contains('\t') && key.Trim().Length > 0;
    }

    static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            var first = trimmed[0];
            var last = trimmed[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return trimmed[1..^1];
        }

        // Комментарий в конце скалярного значения: `token: abc # note`.
        var comment = trimmed.IndexOf(" #", StringComparison.Ordinal);
        if (comment > 0) trimmed = trimmed[..comment].TrimEnd();
        return trimmed;
    }
}
