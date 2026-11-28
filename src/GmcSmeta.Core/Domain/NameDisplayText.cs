using System.Buffers;

namespace GmcSmeta.Core.Domain;

/// <summary>Presentation-only conversion for the compact name display.</summary>
public static class NameDisplayText
{
    private static readonly SearchValues<char> Breaks = SearchValues.Create("\r\n\u0085\u2028\u2029\v\f");

    private static bool IsBreak(char c) => c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029' or '\v' or '\f';

    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var first = value.AsSpan().IndexOfAny(Breaks);
        if (first < 0) return value;

        // Only CRLF shortens the output. Other explicit breaks become one space
        // each. No intermediate replacement strings or cached document text.
        var length = value.Length;
        for (var i = first; i < value.Length - 1; i++)
            if (value[i] == '\r' && value[i + 1] == '\n') { length--; i++; }

        return string.Create(length, (Text: value, First: first), static (destination, state) =>
        {
            state.Text.AsSpan(0, state.First).CopyTo(destination);
            var target = state.First;
            for (var i = state.First; i < state.Text.Length; i++)
            {
                var c = state.Text[i];
                destination[target++] = IsBreak(c) ? ' ' : c;
                if (c == '\r' && i + 1 < state.Text.Length && state.Text[i + 1] == '\n') i++;
            }
        });
    }
}
