using System.Collections;
using System.Globalization;
using System.Reflection;

namespace SockTuner.Services;

public static class InventorySearch
{
    public static bool Matches(object item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var search = query.Trim();
        return Values(item, 0).Any(value => value.Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }

    private static IEnumerable<string> Values(object? value, int depth)
    {
        if (value is null || depth > 4)
        {
            yield break;
        }

        if (value is string text)
        {
            yield return text;
            yield break;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Guid or decimal or DateTime or DateTimeOffset or TimeSpan)
        {
            yield return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            yield break;
        }

        if (value is IEnumerable items)
        {
            foreach (var item in items)
            {
                foreach (var textValue in Values(item, depth + 1))
                {
                    yield return textValue;
                }
            }
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.GetIndexParameters().Length == 0))
        {
            foreach (var textValue in Values(property.GetValue(value), depth + 1))
            {
                yield return textValue;
            }
        }
    }
}
