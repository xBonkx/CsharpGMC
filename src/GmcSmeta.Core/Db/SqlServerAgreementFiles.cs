using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Immutable signed legal PDFs; document attachments remain independent.</summary>
public static class SqlServerAgreementFiles
{
    public static string Latest(SqlConnection conn,long agreementId)
    {
        using var cmd=new SqlCommand("SELECT TOP(1) file_address FROM dbo.agreement_files WHERE agreement_id=@id AND role=N'signed_contract' ORDER BY id DESC",conn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
        return cmd.ExecuteScalar() as string??"";
    }

    public static void AddSignedPdf(string connection,long agreementId,string sourceFile,string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connection);
        using var conn=SqlServerDatabase.Open(connection);
        long objectId;
        using(var owner=new SqlCommand("SELECT object_id FROM dbo.agreements WHERE id=@id AND archived=0",conn))
        {
            owner.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
            objectId=Convert.ToInt64(owner.ExecuteScalar()??throw new InvalidOperationException("Договор не найден."));
        }
        var properties=SqlServerWorkspace.ReadObject(conn,objectId);
        if(string.IsNullOrWhiteSpace(properties.Folder))throw new InvalidOperationException("Сначала укажите папку документов объекта и сохраните реквизиты.");
        using(var input=File.OpenRead(sourceFile))
        {
            Span<byte> signature=stackalloc byte[5];
            if(input.Read(signature)!=5||!signature.SequenceEqual("%PDF-"u8))throw new ArgumentException("Выберите файл PDF.");
        }
        var folder=Path.GetFullPath(Path.Combine(properties.Folder,"Договоры",agreementId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(folder);
        var destination=Path.GetFullPath(Path.Combine(folder,Guid.NewGuid().ToString("N")+".pdf"));
        if(!destination.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Некорректный путь PDF.");
        File.Copy(sourceFile,destination,false);
        var committed=false;
        var commitAttempted=false;
        try
        {
            byte[] hash;
            using(var input=File.OpenRead(destination))hash=SHA256.HashData(input);
            using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
            SqlServerWorkspace.CheckRevision(conn,tx,expectedRevision);
            using(var check=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.agreements a WITH(UPDLOCK,HOLDLOCK) JOIN dbo.objects o WITH(UPDLOCK,HOLDLOCK) ON o.id=a.object_id WHERE a.id=@id AND a.object_id=@object AND a.archived=0 AND o.archived=0 AND o.folder=@folder",conn,tx))
            {
                check.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
                check.Parameters.Add("@object",SqlDbType.BigInt).Value=objectId;
                check.Parameters.Add("@folder",SqlDbType.NVarChar,-1).Value=properties.Folder;
                if(Convert.ToInt64(check.ExecuteScalar())!=1)throw new InvalidOperationException("Договор, объект или папка изменились. Откройте карточку заново.");
            }
            using(var cmd=new SqlCommand("INSERT dbo.agreement_files(agreement_id,role,original_name,file_address,sha256,signed) VALUES(@id,N'signed_contract',@name,@path,@hash,1)",conn,tx))
            {
                cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
                cmd.Parameters.Add("@name",SqlDbType.NVarChar,255).Value=Path.GetFileName(sourceFile);
                cmd.Parameters.Add("@path",SqlDbType.NVarChar,2000).Value=destination;
                cmd.Parameters.Add("@hash",SqlDbType.Binary,32).Value=hash;
                cmd.ExecuteNonQuery();
            }
            SqlServerDatabase.BumpPositionRevision(conn,tx);
            commitAttempted=true;
            tx.Commit();committed=true;
        }
        finally { if(!committed && !commitAttempted) File.Delete(destination); }
    }
}
