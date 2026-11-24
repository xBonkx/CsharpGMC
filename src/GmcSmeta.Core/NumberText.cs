using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core;

/// <summary>
/// Ввод чисел в смете — перенос common.parse_number / formula_text.
/// Принимает «1 234,5», «12*1,15», «=4+5»; возвращает null для текста, бесконечностей,
/// степеней и чисел больше триллиона. Считает на decimal, без двоичных хвостов float.
/// </summary>
public static class NumberText
{
    public const decimal MaxNumber = 1_000_000_000_000m;   // триллион — как MAX_NUMBER в Python

    private static readonly Regex Plain = new(@"^[+-]?(\d+\.?\d*|\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Expression = new(@"^[0-9+\-*/().\s]+$", RegexOptions.CultureInvariant);

    /// <summary>«1 234,5» → 1234.5; «12*1,15» → 13.8; мусор → null.</summary>
    public static decimal? Parse(string? text)
    {
        var t = Normalize(text);
        if (t.Length == 0 || t.Length > 40 || t.Contains("**", StringComparison.Ordinal))
            return null;

        decimal value;
        if (Plain.IsMatch(t))
        {
            if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out value))
                return null;
        }
        else if (Expression.IsMatch(t))
        {
            var parsed = TryEvaluate(t, out value);
            if (!parsed) return null;
        }
        else
        {
            return null;
        }

        if (value == 0m) return 0m;                  // без «-0»
        if (Math.Abs(value) > MaxNumber) return null;
        return value;
    }

    /// <summary>Формула для повторного показа в ячейке: «4+5» → «=4+5»; обычное число → null.</summary>
    public static string? FormulaText(string? text)
    {
        var t = (text ?? "").Trim();
        var body = t.TrimStart('=').Trim();
        if (body.Length == 0) return null;
        var plain = Normalize(body);
        return Plain.IsMatch(plain) ? null : "=" + body;
    }

    /// <summary>Количество для показа/правки: целое без дробной части, иначе до трёх знаков через запятую.</summary>
    public static string QtyText(decimal value)
    {
        if (value == decimal.Truncate(value))
            return decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);
        var text = decimal.Round(value, 3, MidpointRounding.AwayFromZero)
            .ToString("0.###", CultureInfo.InvariantCulture);
        return text.Replace('.', ',');
    }

    /// <summary>Нормализация, общая для числа и формулы: пробелы, запятая, рубль, тире.</summary>
    private static string Normalize(string? text)
    {
        var t = (text ?? "")
            .Replace("\u00a0", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace(",", ".", StringComparison.Ordinal)
            .Replace("₽", "", StringComparison.Ordinal)
            .Replace("−", "-", StringComparison.Ordinal)
            .Replace("–", "-", StringComparison.Ordinal);
        return t.TrimStart('=');
    }

    /// <summary>Только цифры и + - * / ( ). Рекурсивный разбор, без eval и без степеней.</summary>
    private static bool TryEvaluate(string text, out decimal value)
    {
        value = 0m;
        try
        {
            var at = 0;
            var result = ParseSum(text, ref at);
            SkipSpaces(text, ref at);
            if (at != text.Length) return false;
            value = result;
            return true;
        }
        catch (DivideByZeroException) { return false; }
        catch (OverflowException) { return false; }
        catch (FormatException) { return false; }
    }

    private static decimal ParseSum(string text, ref int at)
    {
        var value = ParseProduct(text, ref at);
        while (true)
        {
            SkipSpaces(text, ref at);
            if (at >= text.Length || (text[at] != '+' && text[at] != '-')) return value;
            var op = text[at++];
            var right = ParseProduct(text, ref at);
            value = op == '+' ? value + right : value - right;
        }
    }

    private static decimal ParseProduct(string text, ref int at)
    {
        var value = ParseFactor(text, ref at);
        while (true)
        {
            SkipSpaces(text, ref at);
            if (at >= text.Length || (text[at] != '*' && text[at] != '/')) return value;
            var op = text[at++];
            var right = ParseFactor(text, ref at);
            if (op == '*') value *= right;
            else value = right == 0m ? throw new DivideByZeroException() : value / right;
        }
    }

    private static decimal ParseFactor(string text, ref int at)
    {
        SkipSpaces(text, ref at);
        if (at >= text.Length) throw new FormatException();
        if (text[at] == '+') { at++; return ParseFactor(text, ref at); }
        if (text[at] == '-') { at++; return -ParseFactor(text, ref at); }
        if (text[at] == '(')
        {
            at++;
            var inside = ParseSum(text, ref at);
            SkipSpaces(text, ref at);
            if (at >= text.Length || text[at] != ')') throw new FormatException();
            at++;
            return inside;
        }

        var start = at;
        while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '.')) at++;
        if (start == at) throw new FormatException();
        return decimal.Parse(text[start..at], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private static void SkipSpaces(string text, ref int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
    }
}
