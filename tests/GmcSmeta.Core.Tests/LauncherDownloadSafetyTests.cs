using System.Security.Cryptography;
using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class LauncherDownloadSafetyTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);

    [Fact]
    public void CompleteDownloadReturnsVerifiedHash()
    {
        using var source = new MemoryStream(Bytes);
        using var target = new MemoryStream();
        var expected = Convert.ToHexString(SHA256.HashData(Bytes));
        Assert.Equal(expected, ReleaseDownloadTransfer.CopyVerified(source, target,
            Bytes.Length, expected, CancellationToken.None, Idle));
        Assert.Equal(Bytes, target.ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void IncorrectLengthIsRejectedEvenWithoutHash(long expected)
    {
        using var source = new MemoryStream(Bytes);
        using var target = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => ReleaseDownloadTransfer.CopyVerified(
            source, target, expected, null, CancellationToken.None, Idle));
    }

    [Fact]
    public void IncorrectHashIsRejected()
    {
        using var source = new MemoryStream(Bytes);
        using var target = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => ReleaseDownloadTransfer.CopyVerified(
            source, target, Bytes.Length, new string('0', 64), CancellationToken.None, Idle));
    }

    [Fact]
    public void CancellationBeforeTransferDoesNotWriteBytes()
    {
        using var source = new MemoryStream(Bytes);
        using var target = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => ReleaseDownloadTransfer.CopyVerified(
            source, target, Bytes.Length, null, new CancellationToken(true), Idle));
        Assert.Empty(target.ToArray());
    }

    [Fact]
    public void StalledReadIsCancelled()
    {
        using var source = new StalledStream();
        using var target = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => ReleaseDownloadTransfer.CopyVerified(
            source, target, 1, null, CancellationToken.None, TimeSpan.FromMilliseconds(20)));
        Assert.Empty(target.ToArray());
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
