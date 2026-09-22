namespace WorkUp.BusinessLogic.DemoData;

public static class DemoSeedMultiset
{
    public static bool HasSameRows<T>(IEnumerable<T> actual, IEnumerable<T> expected) where T : notnull
    {
        var remaining = new Dictionary<T, int>();
        foreach (var row in actual)
            remaining[row] = remaining.GetValueOrDefault(row) + 1;

        foreach (var row in expected)
        {
            if (!remaining.TryGetValue(row, out var count))
                return false;
            if (count == 1)
                remaining.Remove(row);
            else
                remaining[row] = count - 1;
        }

        return remaining.Count == 0;
    }
}
