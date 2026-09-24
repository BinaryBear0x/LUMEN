using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Picks the first enabled notification rule whose app pattern appears in the notification's app
/// name (case insensitive) and converts palette hex colours to and from light colours.
/// </summary>
public static class NotificationRuleMatcher
{
    public static NotificationRule? FindMatch(IEnumerable<NotificationRule>? rules, string appName)
    {
        if (rules is null)
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.AppName))
            {
                continue;
            }

            if (appName.Contains(rule.AppName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }
}

public static class HexColor
{
    public static HueRgb Parse(string? hex, HueRgb fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6)
        {
            return fallback;
        }

        try
        {
            return new HueRgb(
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16));
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    public static string Format(HueRgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
