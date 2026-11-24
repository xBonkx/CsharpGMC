using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GmcSmeta.Core.Db;

/// <summary>Length-framed SHA256 for SQL migration rehearsal. NULL, empty values
/// and different cell boundaries must never hash as the same input.</summary>
public sealed class MigrationValueHash : IDisposable
{
    private readonly IncrementalHash _hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;
    public void Append(byte[]? value)
    {
        if(_finished) throw new InvalidOperationException("Fingerprint is complete.");
        Span<byte> length=stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length,value?.LongLength??-1L);
        _hash.AppendData(length);
        if(value is not null) _hash.AppendData(value);
    }
    public string Finish()
    {
        if(_finished) throw new InvalidOperationException("Fingerprint is complete.");
        _finished=true;
        return Convert.ToHexString(_hash.GetHashAndReset());
    }
    public void Dispose()=>_hash.Dispose();
}
