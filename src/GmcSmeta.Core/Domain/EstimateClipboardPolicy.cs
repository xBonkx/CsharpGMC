namespace GmcSmeta.Core.Domain;

/// <summary>The Windows clipboard supersedes stale in-process editor copies.</summary>
public static class EstimateClipboardPolicy
{
    public static bool IsOwned(bool previouslyCopied, string? textWhenCopied, string? actualText) =>
        previouslyCopied && textWhenCopied is not null &&
        string.Equals(textWhenCopied, actualText, StringComparison.Ordinal);

    public static bool IsExternalText(bool previouslyCopied, string? textWhenCopied, string? actualText) =>
        !string.IsNullOrWhiteSpace(actualText) && !IsOwned(previouslyCopied, textWhenCopied, actualText);
}
