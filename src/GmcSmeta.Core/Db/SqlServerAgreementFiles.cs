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

    /// <summary>Verify the exact published file, not user-editable shell input.
    /// SHA-256 confirms content integrity, not a cryptographic signature.</summary>
    public static string VerifyLatest(string connection, long objectId, long agreementId,
        string expectedPath, string expectedRevision)
    {
        using var conn = SqlServerDatabase.Open(connection);
        var before = SqlServerDatabase.Revision(conn);
        if (string.IsNullOrWhiteSpace(before) || before != expectedRevision)
            throw new InvalidOperationException("Карточка изменилась. Откройте её заново.");
        string path;
        byte[] hash;
        using (var cmd = new SqlCommand(
            "SELECT TOP(1) f.file_address,f.sha256 FROM dbo.agreement_files f " +
            "JOIN dbo.agreements a ON a.id=f.agreement_id JOIN dbo.objects o ON o.id=a.object_id " +
            "WHERE a.id=@id AND a.object_id=@object AND a.archived=0 AND o.archived=0 " +
            "AND f.role=N'signed_contract' AND f.signed=1 ORDER BY f.id DESC", conn))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
            cmd.Parameters.Add("@object",SqlDbType.BigInt).Value=objectId;
            using var reader=cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Подписанный PDF не найден.");
            path=reader.GetString(0);
            hash=(byte[])reader.GetValue(1);
        }
        if (!string.Equals(path,expectedPath,StringComparison.Ordinal))
            throw new InvalidOperationException("Подписанный PDF изменился. Откройте карточку заново.");
        AgreementPdfIntegrity.Verify(path,hash);
        if (before != SqlServerDatabase.Revision(conn))
            throw new InvalidOperationException("Карточка изменилась во время проверки PDF.");
        return path;
    }

    public static void AddSignedPdf(string connection,long agreementId,string sourceFile,string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connection);
        AgreementPdfIntegrity.RequirePdfPath(sourceFile);
        var original=Path.GetFileName(sourceFile);
        if (original.Length > 255) throw new ArgumentException("Слишком длинное имя PDF (до 255 символов).");
        using var conn=SqlServerDatabase.Open(connection);
        var before=SqlServerDatabase.Revision(conn);
        if (string.IsNullOrWhiteSpace(before) || before != expectedRevision)
            throw new InvalidOperationException("Карточка изменилась. Откройте её заново.");
        long objectId;
        using(var owner=new SqlCommand(
            "SELECT a.object_id FROM dbo.agreements a JOIN dbo.objects o ON o.id=a.object_id " +
            "WHERE a.id=@id AND a.archived=0 AND o.archived=0",conn))
        {
            owner.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
            objectId=SqlInteger.RequireInt64(owner.ExecuteScalar()??throw new InvalidOperationException("Договор не найден."));
        }
        var properties=SqlServerWorkspace.ReadObject(conn,objectId);
        if(string.IsNullOrWhiteSpace(properties.Folder) || !Path.IsPathFullyQualified(properties.Folder))
            throw new InvalidOperationException("Сначала укажите абсолютную папку документов объекта (локальную или UNC).");
        var folder=Path.GetFullPath(Path.Combine(properties.Folder,"Договоры",agreementId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var destination=Path.Combine(folder,Guid.NewGuid().ToString("N")+".pdf");
        if(destination.Length>2000) throw new ArgumentException("Слишком длинный путь PDF (до 2000 символов).");
        Directory.CreateDirectory(folder);
        var ownedFile=false;
        var commitAttempted=false;
        try
        {
            byte[] hash;
            using(var input=new FileStream(sourceFile,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                ownedFile=true;
                input.CopyTo(output);
                output.Flush(flushToDisk:true);
                output.Position=0;
                hash=AgreementPdfIntegrity.ReadHash(output);
            }
            using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
            SqlServerWorkspace.CheckRevision(conn,tx,expectedRevision);
            using(var check=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.agreements a WITH(UPDLOCK,HOLDLOCK) JOIN dbo.objects o WITH(UPDLOCK,HOLDLOCK) ON o.id=a.object_id WHERE a.id=@id AND a.object_id=@object AND a.archived=0 AND o.archived=0 AND o.folder=@folder",conn,tx))
            {
                check.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
                check.Parameters.Add("@object",SqlDbType.BigInt).Value=objectId;
                check.Parameters.Add("@folder",SqlDbType.NVarChar,-1).Value=properties.Folder;
                if(SqlInteger.RequireInt64(check.ExecuteScalar())!=1)throw new InvalidOperationException("Договор, объект или папка изменились. Откройте карточку заново.");
            }
            using(var duplicate=new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.agreement_files WITH(UPDLOCK,HOLDLOCK) " +
                "WHERE agreement_id=@id AND role=N'signed_contract' AND sha256=@hash",conn,tx))
            {
                duplicate.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
                duplicate.Parameters.Add("@hash",SqlDbType.Binary,32).Value=hash;
                if(SqlInteger.RequireInt64(duplicate.ExecuteScalar())>0)
                    throw new InvalidOperationException("Такой PDF уже зарегистрирован у этого договора.");
            }
            using(var cmd=new SqlCommand("INSERT dbo.agreement_files(agreement_id,role,original_name,file_address,sha256,signed) VALUES(@id,N'signed_contract',@name,@path,@hash,1)",conn,tx))
            {
                cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=agreementId;
                cmd.Parameters.Add("@name",SqlDbType.NVarChar,255).Value=original;
                cmd.Parameters.Add("@path",SqlDbType.NVarChar,2000).Value=destination;
                cmd.Parameters.Add("@hash",SqlDbType.Binary,32).Value=hash;
                if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Не удалось зарегистрировать PDF.");
            }
            SqlServerDatabase.BumpPositionRevision(conn,tx);
            commitAttempted=true;
            tx.Commit();
        }
        catch(Exception error)
        {
            // Unknown COMMIT: do not delete a file which SQL may reference.
            if(commitAttempted)
                throw new InvalidOperationException("Регистрация PDF не подтверждена. Файл сохранён: "+destination+
                    ". Перед повтором откройте карточку заново и проверьте архив.",error);
            if(ownedFile)
            {
                try { File.Delete(destination); }
                catch(Exception cleanup)
                {
                    throw new IOException("Регистрация отменена, но не удалось убрать незарегистрированную копию: "+
                        destination,new AggregateException(error,cleanup));
                }
            }
            throw;
        }
    }
}
