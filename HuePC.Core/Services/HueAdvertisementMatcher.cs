using HuePC.Core.Models;
using System.Text.RegularExpressions;

namespace HuePC.Core.Services;

/// <summary>
/// Keeps the discovery flow focused on Philips Hue / Signify advertisements.
/// The Signify company identifier identifies the organization, not a particular Hue model.
/// </summary>
public static partial class HueAdvertisementMatcher
{
    public const ushort SignifyCompanyIdentifier = 0xFE0F;
    public const string SignifyMemberServiceUuid = "0000FE0F-0000-1000-8000-00805F9B34FB";

    [GeneratedRegex(@"(?<![\p{L}\p{N}])hue(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HueNamePattern();

    public static HueAdvertisementMatch Match(string? advertisedName, IEnumerable<ManufacturerDataEntry> manufacturerData)
    {
        return Match(advertisedName, manufacturerData.Any(entry => entry.CompanyId == SignifyCompanyIdentifier), []);
    }

    public static HueAdvertisementMatch Match(string? advertisedName, bool hasSignifyCompanyId)
    {
        return Match(advertisedName, hasSignifyCompanyId, []);
    }

    public static HueAdvertisementMatch Match(string? advertisedName, bool hasSignifyCompanyId, IEnumerable<string> advertisedServiceUuids)
    {
        var hasHueName = !string.IsNullOrWhiteSpace(advertisedName) && HueNamePattern().IsMatch(advertisedName);
        var hasSignifyServiceUuid = advertisedServiceUuids.Any(uuid => string.Equals(
            uuid,
            SignifyMemberServiceUuid,
            StringComparison.OrdinalIgnoreCase));

        if (hasHueName && hasSignifyCompanyId && hasSignifyServiceUuid)
        {
            return new HueAdvertisementMatch(true, "Hue adı, Signify servis UUID'si ve şirket kimliği (0xFE0F)");
        }

        if (hasSignifyServiceUuid)
        {
            return new HueAdvertisementMatch(true, "Signify'a atanmış servis UUID'si (0xFE0F)");
        }

        if (hasHueName && hasSignifyCompanyId)
        {
            return new HueAdvertisementMatch(true, "Hue adı ve Signify şirket kimliği (0xFE0F)");
        }

        if (hasHueName)
        {
            return new HueAdvertisementMatch(true, "BLE reklamında Hue adı");
        }

        // A Signify company identifier alone is too broad to claim this is a Hue light.
        return new HueAdvertisementMatch(false, null);
    }
}

public sealed record HueAdvertisementMatch(bool IsCandidate, string? Reason);
