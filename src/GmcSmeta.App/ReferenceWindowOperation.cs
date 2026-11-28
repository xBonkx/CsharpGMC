using System;
using System.Windows;

namespace GmcSmeta.App;

/// <summary>One UI-owned operation through the SQL receipt and readback.
/// Disable only the content, not the owner Window needed by modal prompts.</summary>
internal sealed class ReferenceWindowOperation
{
    internal bool IsBusy { get; private set; }

    internal IDisposable? TryEnter(Window owner)
    {
        owner.Dispatcher.VerifyAccess();
        if (IsBusy || owner.Content is not UIElement content) return null;
        var wasEnabled = content.IsEnabled;
        IsBusy = true;
        content.IsEnabled = false;
        return new Lease(this, content, wasEnabled);
    }

    private sealed class Lease(ReferenceWindowOperation operation, UIElement content, bool wasEnabled) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            content.Dispatcher.VerifyAccess();
            operation.IsBusy = false;
            content.IsEnabled = wasEnabled;
        }
    }
}
