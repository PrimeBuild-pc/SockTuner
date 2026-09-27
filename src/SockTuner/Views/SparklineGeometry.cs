using System.Windows;

namespace SockTuner.Views;

internal static class SparklineGeometry
{
    internal static IReadOnlyList<IReadOnlyList<Point>> Build(
        IReadOnlyList<double?> values, double width = 600, double height = 120, double padding = 6)
    {
        if (values.Count == 0 || width <= padding * 2 || height <= padding * 2) return [];

        var valid = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (valid.Length == 0) return [];

        var minimum = valid.Min();
        var maximum = valid.Max();
        var range = maximum - minimum;
        var segments = new List<IReadOnlyList<Point>>();
        var current = new List<Point>();
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not { } value)
            {
                if (current.Count > 0) segments.Add(current.ToArray());
                current.Clear();
                continue;
            }

            var x = values.Count == 1 ? width / 2 : index * width / (values.Count - 1);
            var y = range == 0
                ? height / 2
                : padding + ((maximum - value) / range * (height - padding * 2));
            current.Add(new Point(x, y));
        }

        if (current.Count > 0) segments.Add(current.ToArray());
        return segments;
    }
}
