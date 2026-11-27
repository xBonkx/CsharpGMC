using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// PTO ZIP v2: SQL Server JSON snapshot and immutable file tree. Never serializes a
/// SQLite database; never extracts untrusted paths without strict validation.
/// </summary>
public static class PtoBackup
{
    private const string Meta="pto-sql-snapshot.json";
    private const string Marker="pto-archive.json";
    private const long MaxSize=2_000_000_000L;
    private const int MaxFiles=50000;
    private static readonly JsonSerializerOptions Json=new(PtoRules.Json){WriteIndented=true};

    public static void Create(PtoArchive archive,string destination,string connection)
    {
        var snapshot=SqlServerPtoBackup.Export(connection);
        CreateFromSnapshot(archive,destination,snapshot);
    }
    private static void CreateFromSnapshot(PtoArchive archive,string destination,PtoSqlSnapshot data)
    {
        var output=Path.GetFullPath(destination);
        var root=archive.Root.TrimEnd(Path.DirectorySeparatorChar);
        if(output.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Сохраните ZIP вне архива ПТО.");
        foreach(var pair in Referenced(data))
        {
            var path=archive.FilePath(pair.Path);
            if(!File.Exists(path)||
               !PtoArchive.Sha(path).Equals(pair.Sha,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Оригинал отсутствует или повреждён: "+pair.Path);
        }
        // Preserve the original create-new contract, including concurrent collisions.
        using var prepared=ExportFilePublication.Stage(output,stream=>WriteArchive(stream,archive,root,data));
        prepared.Publish(overwrite:false);
    }

    private static void WriteArchive(Stream stream,PtoArchive archive,string root,PtoSqlSnapshot data)
    {
        using var zip=new ZipArchive(stream,ZipArchiveMode.Create,leaveOpen:true);
        void Write(string name,string text)
        {
            using var writer=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Optimal).Open(),Encoding.UTF8);
            writer.Write(text);
        }
        Write(Marker,JsonSerializer.Serialize(new {format=2,backend="Microsoft SQL Server"}));
        Write(Meta,JsonSerializer.Serialize(data,Json));
        long total=0;int count=0;
        foreach(var directory in new[]{"files","templates","exports"})
        {
            var folder=Path.Combine(root,directory);
            if(!Directory.Exists(folder))continue;
            foreach(var path in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Ссылки в архиве не допускаются: "+path);
                total+=new FileInfo(path).Length;
                if(total>MaxSize || ++count>MaxFiles)throw new InvalidDataException("Архив ПТО слишком большой.");
                var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                archive.FilePath(relative);
                zip.CreateEntryFromFile(path,relative,CompressionLevel.Optimal);
            }
        }
    }

    private sealed record SourceFile(string Path,string Sha);
    private static IEnumerable<SourceFile> Referenced(PtoSqlSnapshot data)
    {
        foreach(var row in data.Packages)
        {
            var p=JsonSerializer.Deserialize<PtoPackage>(row.Json,PtoRules.Json)!;
            foreach(var a in p.Attachments)
                yield return new SourceFile(a.RelativePath,a.Sha256);
        }
        foreach(var row in data.Certificates)
        {
            var c=JsonSerializer.Deserialize<PtoCertificate>(row.Json,PtoRules.Json)!;
            yield return new SourceFile(c.RelativePath,c.Sha256);
        }
        foreach(var row in data.Releases)
            yield return new SourceFile(row.RelativePath.TrimEnd('/')+"/Состав_выпуска.json",row.Sha256);
    }
    private static void CheckZip(ZipArchive zip)
    {
        if(zip.Entries.Count>MaxFiles+2)throw new InvalidDataException("В ZIP слишком много файлов.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size=0;
        foreach(var entry in zip.Entries)
        {
            var name=entry.FullName;
            if(!names.Add(name))throw new InvalidDataException("Дублированный путь в ZIP: "+name);
            var parts=name.Split('/');
            var isDirectory=name.EndsWith('/');
            if(name.StartsWith('/')||name.Contains('\\')||name.Contains(':')||
               parts.Take(isDirectory?parts.Length-1:parts.Length)
                   .Any(p=>p is "." or ".." or ""))
                throw new InvalidDataException("Небезопасный путь ZIP: "+name);
            var prefix=parts[0];
            if(prefix!=Marker && prefix!=Meta && prefix is not ("files" or "templates" or "exports"))
                throw new InvalidDataException("Неизвестная область в ZIP: "+name);
            if((prefix==Marker||prefix==Meta) && name!=prefix)
                throw new InvalidDataException("Метаданные ZIP разрешены только в корне архива.");
            if((entry.ExternalAttributes>>16 & 0xF000)==0xA000)
                throw new InvalidDataException("ZIP содержит символическую ссылку: "+name);
            size+=entry.Length;
            if(size>MaxSize)throw new InvalidDataException("Распакованный архив ПТО слишком большой.");
            if(entry.CompressedLength>0 && entry.Length/Math.Max(1,entry.CompressedLength)>10000 &&
               entry.Length>10_000_000)
                throw new InvalidDataException("Подозрительное сжатие ZIP.");
        }
    }
    /// <summary>Validate archive paths before the UI asks for a restore target.</summary>
    public static void ValidateZip(string source)
    {
        using var zip=ZipFile.OpenRead(source);
        CheckZip(zip);
        if(zip.GetEntry(Meta) is null||zip.GetEntry(Marker) is null)
            throw new InvalidDataException("Нет метаданных SQL/маркера формата в ZIP архиве ПТО.");
    }

    public static void Restore(string connection,string backup,string target)
    {
        SqlServerWorkspace.RequirePilotDatabase(connection);
        var dest=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var app=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if(dest.Equals(app,StringComparison.OrdinalIgnoreCase)||
           dest.StartsWith(app+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Архив ПТО нельзя восстанавливать в папку программы.");
        if(Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new InvalidOperationException("Для восстановления выберите новую пустую папку.");
        using var zip=ZipFile.OpenRead(backup);
        CheckZip(zip);
        var meta=zip.GetEntry(Meta)??throw new InvalidDataException("Не найден JSON снимок SQL Server.");
        var marker=zip.GetEntry(Marker)??throw new InvalidDataException("Не найдена версия архива ПТО.");
        using(var reader=new StreamReader(marker.Open(),Encoding.UTF8))
        using(var json=JsonDocument.Parse(reader.ReadToEnd()))
            if(json.RootElement.GetProperty("format").GetInt32()!=2 ||
               json.RootElement.GetProperty("backend").GetString()!="Microsoft SQL Server")
                throw new InvalidDataException("Ожидается новая SQL Server ZIP-копия ПТО, не pto.sqlite3.");
        PtoSqlSnapshot snapshot;
        using(var reader=new StreamReader(meta.Open(),Encoding.UTF8))
            snapshot=JsonSerializer.Deserialize<PtoSqlSnapshot>(reader.ReadToEnd(),PtoRules.Json)
                ?? throw new InvalidDataException("Повреждён JSON SQL Server.");
        var parent=Path.GetDirectoryName(dest)??throw new InvalidOperationException("Нет корня.");
        Directory.CreateDirectory(parent);
        var stage=Path.Combine(parent,".pto-restore-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var published=false;
        try
        {
            foreach(var entry in zip.Entries)
            {
                if(entry.FullName.EndsWith('/'))continue;
                if(entry.FullName is Marker or Meta)continue;
                var file=Path.GetFullPath(Path.Combine(stage,
                    entry.FullName.Replace('/',Path.DirectorySeparatorChar)));
                if(!file.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ZIP содержит путь за пределами восстановления.");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var input=entry.Open();
                using var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                input.CopyTo(output);
            }
            foreach(var file in Referenced(snapshot))
            {
                var full=Path.GetFullPath(Path.Combine(stage,file.Path.Replace('/',Path.DirectorySeparatorChar)));
                if(!full.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                   !File.Exists(full)||
                   !PtoArchive.Sha(full).Equals(file.Sha,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Не совпал SHA-256 восстановленного файла: "+file.Path);
            }
            if(Directory.Exists(dest))Directory.Delete(dest);
            Directory.Move(stage,dest);published=true;
            try{SqlServerPtoBackup.Import(connection,snapshot);}
            catch(PtoCommitUncertainException ex)
            {
                throw new PtoCommitUncertainException(ex.Message+"\nВосстановленные файлы сохранены: "+dest,ex);
            }
            catch
            {
                if(Directory.Exists(dest))Directory.Delete(dest,true);
                throw;
            }
        }
        finally
        {
            if(!published && Directory.Exists(stage))Directory.Delete(stage,true);
        }
    }
}
