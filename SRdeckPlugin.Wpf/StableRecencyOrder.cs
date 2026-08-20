using System.Collections.ObjectModel;

namespace SRdeckPlugin.Wpf;

/// <summary>
/// Keeps recency lists stable while still promoting entries that are clearly newer.
/// Existing entries never cross each other when their timestamps differ by at most
/// the configured tolerance.
/// </summary>
public static class StableRecencyOrder
{
    public static TimeSpan DefaultTolerance { get; } = TimeSpan.FromSeconds(10);

    public static void Reorder<T>(
        ObservableCollection<T> target,
        Func<T, DateTimeOffset> timestampSelector,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(timestampSelector);

        TimeSpan effectiveTolerance = ValidateTolerance(tolerance);
        List<T> ordered = target.ToList();
        PromoteClearlyNewerItems(ordered, timestampSelector, effectiveTolerance);

        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            int currentIndex = target.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex) target.Move(currentIndex, targetIndex);
        }
    }

    public static void Replace<T, TKey>(
        ObservableCollection<T> target,
        IEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, DateTimeOffset> timestampSelector,
        TimeSpan? tolerance = null)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(timestampSelector);

        TimeSpan effectiveTolerance = ValidateTolerance(tolerance);
        T[] incoming = source.ToArray();
        Dictionary<TKey, T> incomingByKey = incoming.ToDictionary(keySelector);
        var retainedKeys = new HashSet<TKey>();
        var ordered = new List<T>(incoming.Length);

        // Replace the values but retain the previous relative order for known keys.
        foreach (T existing in target)
        {
            TKey key = keySelector(existing);
            if (retainedKeys.Add(key) && incomingByKey.TryGetValue(key, out T? replacement))
                ordered.Add(replacement);
        }

        // A newly discovered item has no previous position, so place it by exact time.
        foreach (T item in incoming.OrderByDescending(timestampSelector))
        {
            if (!retainedKeys.Add(keySelector(item))) continue;
            int insertionIndex = ordered.FindIndex(existing =>
                timestampSelector(item) > timestampSelector(existing));
            if (insertionIndex < 0) ordered.Add(item);
            else ordered.Insert(insertionIndex, item);
        }

        PromoteClearlyNewerItems(ordered, timestampSelector, effectiveTolerance);
        Reconcile(target, ordered, keySelector);
    }

    private static void PromoteClearlyNewerItems<T>(
        List<T> ordered,
        Func<T, DateTimeOffset> timestampSelector,
        TimeSpan tolerance)
    {
        for (int currentIndex = 1; currentIndex < ordered.Count; currentIndex++)
        {
            T item = ordered[currentIndex];
            DateTimeOffset timestamp = timestampSelector(item);
            int insertionIndex = currentIndex;
            while (insertionIndex > 0 &&
                   timestamp - timestampSelector(ordered[insertionIndex - 1]) > tolerance)
            {
                insertionIndex--;
            }

            if (insertionIndex == currentIndex) continue;
            ordered.RemoveAt(currentIndex);
            ordered.Insert(insertionIndex, item);
        }
    }

    private static void Reconcile<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> ordered,
        Func<T, TKey> keySelector)
        where T : class
        where TKey : notnull
    {
        EqualityComparer<TKey> comparer = EqualityComparer<TKey>.Default;
        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            T desired = ordered[targetIndex];
            TKey desiredKey = keySelector(desired);
            int currentIndex = -1;
            for (int index = targetIndex; index < target.Count; index++)
            {
                if (!comparer.Equals(keySelector(target[index]), desiredKey)) continue;
                currentIndex = index;
                break;
            }

            if (currentIndex < 0) target.Insert(targetIndex, desired);
            else if (currentIndex != targetIndex) target.Move(currentIndex, targetIndex);

            if (!ReferenceEquals(target[targetIndex], desired)) target[targetIndex] = desired;
        }

        while (target.Count > ordered.Count) target.RemoveAt(target.Count - 1);
    }

    private static TimeSpan ValidateTolerance(TimeSpan? tolerance)
    {
        TimeSpan value = tolerance ?? DefaultTolerance;
        if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tolerance));
        return value;
    }
}
