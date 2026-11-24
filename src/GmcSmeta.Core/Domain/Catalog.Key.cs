using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Провайдер-независимая часть каталога — перенос services.catalog_key:
/// ключ не зависит от регистра, лишних пробелов и точки в конце единицы.
/// Легаси-чтение SQLite (Unit/ItemId) остаётся в Catalog.cs.
/// </summary>
public static partial class Catalog
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Нормализованное имя: неразрывный пробел, повторные пробелы и регистр не важны.</summary>
    public static string NormalizeName(string? name) =>
        Spaces.Replace((name ?? "").Replace('\u00a0', ' '), " ").Trim().ToLowerInvariant();

    /// <summary>Ключ каталога «имя|единица».</summary>
    public static string Key(string? name, string? unit) =>
        $"{NormalizeName(name)}|{(unit ?? "").Trim().ToLowerInvariant().TrimEnd('.')}";
}
