namespace SRdeckPlugin.Wpf;

/// <summary>Received information for everyday monitoring, independent of decoder diagnostics.</summary>
public sealed record ReceptionOverview(string Summary, string Scope,
    IReadOnlyList<ReceptionOverviewMetric> Metrics, IReadOnlyList<ReceptionOverviewFact> Facts,
    string Note, string DetailHint)
{
    public static string DescribeScope(string source, IEnumerable<DateTimeOffset> timestamps)
    {
        DateTimeOffset[] times = timestamps.ToArray();
        if (times.Length == 0) return $"集計対象: {source}なし。受信または履歴の読み込み後に表示します。";
        DateTimeOffset first = times.Min().ToLocalTime();
        DateTimeOffset last = times.Max().ToLocalTime();
        return $"集計対象: {source} {times.Length:N0}件 / {first:yyyy/MM/dd HH:mm:ss} ～ {last:yyyy/MM/dd HH:mm:ss}（ローカル時刻）";
    }

    public static IReadOnlyList<ReceptionOverviewFact> CountKinds(IEnumerable<string> kinds)
    {
        var groups = kinds.GroupBy(kind => string.IsNullOrWhiteSpace(kind) ? "種別不明" : kind)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        List<ReceptionOverviewFact> facts = groups.Take(4)
            .Select(group => new ReceptionOverviewFact(group.Key, $"{group.Count():N0}件")).ToList();
        if (groups.Length > 4)
            facts.Add(new("その他の種別", $"{groups.Skip(4).Sum(group => group.Count()):N0}件"));
        if (facts.Count == 0) facts.Add(new("通信の内訳", "保持データなし"));
        return facts;
    }
}

public sealed record ReceptionOverviewMetric(string Label, int Count)
{
    public string Value => Count.ToString("N0");
}

public sealed record ReceptionOverviewFact(string Label, string Value);
