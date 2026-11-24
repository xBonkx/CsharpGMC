using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;
public sealed class MigrationValueHashTests
{
    private static string Digest(params byte[]?[] cells)
    {
        using var hash=new MigrationValueHash();
        foreach(var cell in cells) hash.Append(cell);
        return hash.Finish();
    }

    [Fact]
    public void StableAndUnambiguous()
    {
        Assert.Equal(Digest(new byte[]{1,2},new byte[]{3}),Digest(new byte[]{1,2},new byte[]{3}));
        Assert.NotEqual(Digest(new byte[]{1,2},new byte[]{3}),Digest(new byte[]{1},new byte[]{2,3}));
        Assert.NotEqual(Digest((byte[]?)null),Digest(Array.Empty<byte>()));
        Assert.NotEqual(Digest(new byte[]{1},new byte[]{2}),Digest(new byte[]{2},new byte[]{1}));
    }
}
