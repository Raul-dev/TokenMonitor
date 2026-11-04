using System.Globalization;

namespace TokenMonitor;

public sealed class DevinStore
{
    readonly AppSettings settings;
    readonly DevinClient client;

    DateTime lastFetch = DateTime.MinValue;
    DevinUsageMetrics? usageMetrics;
    DevinSessionMetrics? sessionMetrics;
    DevinCategoryMetrics? categoryMetrics;

    public DevinStore(AppSettings settings)
    {
        this.settings = settings;
        client = new DevinClient(settings);
    }

    public bool IsConfigured => client.IsConfigured;

    public async Task<DevinSnapshot> LoadAsync(CancellationToken token = default)
    {
        var now = DateTime.UtcNow;
        var shouldRefresh = lastFetch == DateTime.MinValue || (now - lastFetch).TotalSeconds >= settings.BudgetRefreshSeconds;

        if (shouldRefresh)
        {
            lastFetch = now;

            var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            var monthStartMs = monthStart.ToUnixTimeMilliseconds();
            var nowMs = ((DateTimeOffset)now).ToUnixTimeMilliseconds();

            var usageTask = client.GetUsageMetricsAsync(monthStartMs, nowMs, token);
            var sessionTask = client.GetSessionMetricsAsync(token);
            var categoryTask = client.GetCategoryMetricsAsync(monthStartMs, nowMs, token);

            try
            {
                usageMetrics = await usageTask;
            }
            catch
            {
                usageMetrics = new DevinUsageMetrics { Error = "timeout" };
            }

            try
            {
                sessionMetrics = await sessionTask;
            }
            catch
            {
                sessionMetrics = new DevinSessionMetrics { Error = "timeout" };
            }

            try
            {
                categoryMetrics = await categoryTask;
            }
            catch
            {
                categoryMetrics = new DevinCategoryMetrics { Error = "timeout" };
            }
        }

        return new DevinSnapshot
        {
            UsageMetrics = usageMetrics,
            SessionMetrics = sessionMetrics,
            CategoryMetrics = categoryMetrics,
            DbPath = client.IsConfigured ? $"Devin API v3 (org: {settings.DevinOrgId})" : "Devin API не настроен",
            EventsPath = string.Empty,
            LoadedAt = DateTime.Now,
        };
    }

    public static DataSnapshot ToDataSnapshot(DevinSnapshot devinSnapshot)
    {
        var sessions = new List<SessionData>();

        if (devinSnapshot.CategoryMetrics?.Categories is { } categories)
        {
            foreach (var cat in categories)
            {
                var session = new SessionData
                {
                    Id = cat.Category,
                    Title = cat.Category,
                    Directory = string.Empty,
                    Model = "Devin",
                    Created = DateTime.Now,
                    Updated = DateTime.Now,
                    Tokens = new TokenCounts((long)cat.Acus, 0, 0, 0, 0),
                };

                foreach (var sub in cat.Subcategories)
                {
                    var request = new RequestData
                    {
                        Id = sub.SubcategoryId,
                        SessionId = cat.Category,
                        Prompt = sub.DisplayName,
                        Time = DateTime.Now,
                        Session = session,
                        Tokens = new TokenCounts((long)sub.Acus, 0, 0, 0, 0),
                    };

                    request.Steps.Add(new StepData
                    {
                        Id = sub.SubcategoryId,
                        Time = DateTime.Now,
                        Completed = DateTime.Now,
                        Model = "Devin",
                        Tokens = request.Tokens,
                    });

                    session.Requests.Add(request);
                    session.Tokens += request.Tokens;
                }

                sessions.Add(session);
            }
        }

        var signature = (long)sessions.Count;
        if (sessions.Count > 0) signature = signature * 31 + sessions.Sum(s => s.Tokens.Total);

        return new DataSnapshot
        {
            Sessions = sessions,
            Live = new List<LiveEvent>(),
            Signature = signature,
            DbPath = devinSnapshot.DbPath,
            EventsPath = devinSnapshot.EventsPath,
            LoadedAt = devinSnapshot.LoadedAt,
        };
    }
}

public sealed class DevinSnapshot
{
    public DevinUsageMetrics? UsageMetrics { get; init; }
    public DevinSessionMetrics? SessionMetrics { get; init; }
    public DevinCategoryMetrics? CategoryMetrics { get; init; }
    public string DbPath { get; init; } = string.Empty;
    public string EventsPath { get; init; } = string.Empty;
    public DateTime LoadedAt { get; init; }
}
