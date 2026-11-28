namespace GmcSmeta.Core.Domain;

/// <summary>The Windows clipboard supersedes stale in-process editor copies.</summary>
public static class EstimateClipboardPolicy
{
    public static bool IsOwned(bool previouslyCopied, string? textWhenCopied, string? actualText,
        string? tokenWhenCopied, string? actualToken) =>
        previouslyCopied && textWhenCopied is not null &&
        !string.IsNullOrEmpty(tokenWhenCopied) &&
        string.Equals(tokenWhenCopied, actualToken, StringComparison.Ordinal) &&
        string.Equals(textWhenCopied, actualText, StringComparison.Ordinal);

    public static bool IsExternalText(bool previouslyCopied, string? textWhenCopied, string? actualText,
        string? tokenWhenCopied, string? actualToken) =>
        !string.IsNullOrWhiteSpace(actualText) &&
        !IsOwned(previouslyCopied, textWhenCopied, actualText, tokenWhenCopied, actualToken);
}
