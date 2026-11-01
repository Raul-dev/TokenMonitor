using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TokenMonitor;

public readonly record struct TokenCounts(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite)
{
    public static readonly TokenCounts Zero = new(0, 0, 0, 0, 0);

    public long Total => Input + Output + Reasoning + CacheRead + CacheWrite;
    public long Cached => CacheRead + CacheWrite;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.Reasoning + b.Reasoning,
        a.CacheRead + b.CacheRead,
        a.CacheWrite + b.CacheWrite);
}

public abstract class Node : INotifyPropertyChanged
{
    bool isExpanded;
    bool isSelected;

    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public TokenCounts Tokens { get; init; }

    public string TotalText => Fmt.Tokens(Tokens.Total);
    public string InputText => Tokens.Input == 0 ? "—" : Fmt.Tokens(Tokens.Input);
    public string OutputText => Tokens.Output == 0 ? "—" : Fmt.Tokens(Tokens.Output);
    public string ReasoningText => Tokens.Reasoning == 0 ? "—" : Fmt.Tokens(Tokens.Reasoning);
    public string CacheText => Tokens.Cached == 0 ? "—" : Fmt.Tokens(Tokens.Cached);
    public string ExactText =>
        $"вход {Fmt.Exact(Tokens.Input)} · выход {Fmt.Exact(Tokens.Output)} · reasoning {Fmt.Exact(Tokens.Reasoning)}\n" +
        $"кэш чтение {Fmt.Exact(Tokens.CacheRead)} · кэш запись {Fmt.Exact(Tokens.CacheWrite)}\n" +
        $"итого {Fmt.Exact(Tokens.Total)}";

    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded == value) return; isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected == value) return; isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SessionNode : Node
{
    public required SessionData Session { get; init; }
    public ObservableCollection<Node> Children { get; } = new();
}

public sealed class RequestNode : Node
{
    public required RequestData Request { get; init; }
    public required SessionData Session { get; init; }
    public ObservableCollection<Node> Children { get; } = new();
}

public sealed class StepNode : Node
{
    public required StepData Step { get; init; }
    public required RequestData Request { get; init; }
}

public sealed class SessionData
{
    public string Id { get; init; } = string.Empty;
    public string ParentId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Directory { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public TokenCounts Tokens { get; set; }
    public List<RequestData> Requests { get; } = new();

    public bool IsChild => ParentId.Length != 0;
    public long DurationMs => (long)(Updated - Created).TotalMilliseconds;
}

public sealed class RequestData
{
    public string Id { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public DateTime Time { get; set; }
    public TokenCounts Tokens { get; set; }
    public SessionData Session { get; set; } = null!;
    public List<StepData> Steps { get; } = new();

    public long DurationMs => Steps.Count == 0
        ? 0
        : (long)(Steps[^1].Completed - Time).TotalMilliseconds;
}

public sealed class StepData
{
    public string Id { get; init; } = string.Empty;
    public DateTime Time { get; init; }
    public DateTime Completed { get; init; }
    public string Model { get; init; } = string.Empty;
    public TokenCounts Tokens { get; init; }

    public long DurationMs => (long)(Completed - Time).TotalMilliseconds;
}

public sealed class LiveEvent
{
    public DateTime Time { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int Steps { get; init; }
    public long Input { get; init; }
    public long Output { get; init; }
    public long Reasoning { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long Billed { get; init; }
    public long Context { get; init; }
    public long DurationMs { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public string DateText => Time.ToString("dd.MM HH:mm");
    public string BilledText => Fmt.Tokens(Billed);
    public string DurationText => Fmt.Duration(DurationMs);
    public string StepsText => Steps.ToString();
    public string SessionText => SessionId.Replace("ses_", "");
    public string ExactText =>
        $"запрос: {Time:dd.MM.yyyy HH:mm:ss}\n" +
        $"вход {Fmt.Exact(Input)} · выход {Fmt.Exact(Output)} · reasoning {Fmt.Exact(Reasoning)}\n" +
        $"кэш чтение {Fmt.Exact(CacheRead)} · кэш запись {Fmt.Exact(CacheWrite)}\n" +
        $"итого {Fmt.Exact(Billed)} · контекст {Fmt.Exact(Context)} · шагов {Steps}";
}
