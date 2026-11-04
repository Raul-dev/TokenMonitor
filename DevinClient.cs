using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TokenMonitor;

public sealed class DevinUsageMetrics
{
    public int SessionsCount { get; init; }
    public int PrsCreatedCount { get; init; }
    public int PrsMergedCount { get; init; }
    public int SearchesCount { get; init; }
    public string? Error { get; init; }
}

public sealed class DevinSessionMetrics
{
    public double AvgAcusPerSession { get; init; }
    public int SessionsCreatedCount { get; init; }
    public int SessionsWithMergedPrsCount { get; init; }
    public Dictionary<string, int> SessionsCreatedByOrigin { get; init; } = new();
    public Dictionary<string, int> SessionsCreatedBySize { get; init; } = new();
    public string? Error { get; init; }
}

public sealed class DevinCategoryMetrics
{
    public List<DevinCategory> Categories { get; init; } = new();
    public string? Error { get; init; }
}

public sealed class DevinCategory
{
    public double Acus { get; init; }
    public string Category { get; init; } = string.Empty;
    public int SessionsCount { get; init; }
    public List<DevinSubcategory> Subcategories { get; init; } = new();
}

public sealed class DevinSubcategory
{
    public double Acus { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int SessionsCount { get; init; }
    public string SubcategoryId { get; init; } = string.Empty;
}

public sealed class DevinClient
{
    static readonly HttpClient Http = CreateClient();

    readonly AppSettings settings;

    public DevinClient(AppSettings settings) => this.settings = settings;

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
        };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.DevinApiKey) && !string.IsNullOrWhiteSpace(settings.DevinOrgId);

    public async Task<DevinUsageMetrics> GetUsageMetricsAsync(long? timeAfter = null, long? timeBefore = null, CancellationToken token = default)
    {
        if (!IsConfigured)
        {
            return new DevinUsageMetrics { Error = "не настроен Devin API ключ или Org ID" };
        }

        try
        {
            var url = BuildUrl("https://api.devin.ai/v3/enterprise/metrics/usage",
                ("time_after", timeAfter?.ToString()),
                ("time_before", timeBefore?.ToString()));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request);

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new DevinUsageMetrics { Error = $"usage metrics {(int)response.StatusCode}" };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return new DevinUsageMetrics
            {
                SessionsCount = Int(root, "sessions_count"),
                PrsCreatedCount = Int(root, "prs_created_count"),
                PrsMergedCount = Int(root, "prs_merged_count"),
                SearchesCount = Int(root, "searches_count"),
            };
        }
        catch (Exception ex)
        {
            return new DevinUsageMetrics { Error = ex.Message };
        }
    }

    public async Task<DevinSessionMetrics> GetSessionMetricsAsync(CancellationToken token = default)
    {
        if (!IsConfigured)
        {
            return new DevinSessionMetrics { Error = "не настроен Devin API ключ или Org ID" };
        }

        try
        {
            var url = $"https://api.devin.ai/v3/organizations/{settings.DevinOrgId}/metrics/sessions";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request);

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new DevinSessionMetrics { Error = $"session metrics {(int)response.StatusCode}" };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var origin = new Dictionary<string, int>();
            if (root.TryGetProperty("sessions_created_by_origin", out var originEl))
            {
                foreach (var prop in originEl.EnumerateObject())
                {
                    origin[prop.Name] = prop.Value.GetInt32();
                }
            }

            var size = new Dictionary<string, int>();
            if (root.TryGetProperty("sessions_created_by_size", out var sizeEl))
            {
                foreach (var prop in sizeEl.EnumerateObject())
                {
                    size[prop.Name] = prop.Value.GetInt32();
                }
            }

            return new DevinSessionMetrics
            {
                AvgAcusPerSession = Double(root, "avg_acus_per_session") ?? 0,
                SessionsCreatedCount = Int(root, "sessions_created_count"),
                SessionsWithMergedPrsCount = Int(root, "sessions_with_merged_prs_count"),
                SessionsCreatedByOrigin = origin,
                SessionsCreatedBySize = size,
            };
        }
        catch (Exception ex)
        {
            return new DevinSessionMetrics { Error = ex.Message };
        }
    }

    public async Task<DevinCategoryMetrics> GetCategoryMetricsAsync(long? timeAfter = null, long? timeBefore = null, CancellationToken token = default)
    {
        if (!IsConfigured)
        {
            return new DevinCategoryMetrics { Error = "не настроен Devin API ключ или Org ID" };
        }

        try
        {
            var url = BuildUrl("https://api.devin.ai/v3/enterprise/metrics/sessions-by-category",
                ("time_after", timeAfter?.ToString()),
                ("time_before", timeBefore?.ToString()));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request);

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                return new DevinCategoryMetrics { Error = $"category metrics {(int)response.StatusCode}" };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var categories = new List<DevinCategory>();
            if (root.TryGetProperty("categories", out var catsEl))
            {
                foreach (var catEl in catsEl.EnumerateArray())
                {
                    var subcategories = new List<DevinSubcategory>();
                    if (catEl.TryGetProperty("subcategories", out var subsEl))
                    {
                        foreach (var subEl in subsEl.EnumerateArray())
                        {
                            subcategories.Add(new DevinSubcategory
                            {
                                Acus = Double(subEl, "acus") ?? 0,
                                DisplayName = Text(subEl, "display_name") ?? string.Empty,
                                SessionsCount = Int(subEl, "sessions_count"),
                                SubcategoryId = Text(subEl, "subcategory_id") ?? string.Empty,
                            });
                        }
                    }

                    categories.Add(new DevinCategory
                    {
                        Acus = Double(catEl, "acus") ?? 0,
                        Category = Text(catEl, "category") ?? string.Empty,
                        SessionsCount = Int(catEl, "sessions_count"),
                        Subcategories = subcategories,
                    });
                }
            }

            return new DevinCategoryMetrics { Categories = categories };
        }
        catch (Exception ex)
        {
            return new DevinCategoryMetrics { Error = ex.Message };
        }
    }

    void ApplyAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DevinApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    static string BuildUrl(string baseUrl, params (string? name, string? value)[] queryParams)
    {
        if (queryParams.Length == 0) return baseUrl;

        var query = string.Join("&", queryParams
            .Where(p => !string.IsNullOrWhiteSpace(p.value))
            .Select(p => $"{p.name}={p.value}"));

        return baseUrl + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query);
    }

    static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    static double? Double(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    static string? Text(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
