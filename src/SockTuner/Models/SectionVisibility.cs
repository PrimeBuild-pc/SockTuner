namespace SockTuner.Models;

/// <summary>Which navigation entries are shown: a basic list or everything, never an empty group heading.</summary>
public static class SectionVisibility
{
    public static bool[] Compute(
        IReadOnlyList<(string Name, bool IsHeader)> items,
        bool advanced,
        IReadOnlySet<string> basic,
        string alwaysHidden)
    {
        var visible = new bool[items.Count];
        var header = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsHeader) { header = i; continue; }
            visible[i] = items[i].Name != alwaysHidden && (advanced || basic.Contains(items[i].Name));
            if (visible[i] && header >= 0) visible[header] = true;
        }

        return visible;
    }
}
