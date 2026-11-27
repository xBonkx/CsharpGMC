using System.Globalization;

namespace GmcSmeta.Launcher;

/// <summary>Сравнение версий вида «v1.2.3» без внешних библиотек.</summary>
internal static class AppVersion
{
    public static string Normalize(string tag)
    {
        var value = (tag ?? "").Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        return value;
    }

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var normalized = Normalize(text ?? "");
        if (normalized.Length == 0) return false;
        var parts = normalized.Split('.');
        if (parts.Length > 4) return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;
        }
        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>True, если installed старше available (или installed не разбирается).</summary>
    public static bool IsOlder(string? installed, string available)
    {
        if (!TryParse(available, out var target)) return false;
        if (!TryParse(installed, out var current)) return true;
        return current < target;
    }

    /// <summary>Only a valid numeric release at or above the installed version is eligible.
    /// A differently formatted equal version is not a newer version.</summary>
    public static bool CanOfferRelease(string? installed, string available)
    {
        if (!IsNumericRelease(available) || !TryParse(available, out var target))
            return false;
        if (installed is null) return true;
        return IsNumericRelease(installed) && TryParse(installed, out var current) && target >= current;
    }

    private static bool IsNumericRelease(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiDigit(c) || c == '.') &&
        Version.TryParse(value, out _);

    public static string CurrentLauncher()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        var info = assembly?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
