using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GmcSmeta.App;

/// <summary>
/// Immutable originals + all-or-nothing delivery folders on a shared NTFS/SMB root.
/// SQL owns metadata; file archive never creates or opens pto.sqlite3.
/// </summary>
public sealed class PtoArchive
{
    public SqlServerPto Sql {get;}
    public string Root {get;}
    public PtoArchive(string connectionString,string? root=null)
    {
        Sql=new SqlServerPto(connectionString);
        Root=Path.GetFullPath(root??Environment.GetEnvironmentVariable("GMC_SMETA_PTO_ROOT")??
            Path.Combine(GmcSmeta.Core.Demo.DemoMode.LocalData,
            "GMC-Smeta-Next","PTO"));
        var app=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if(Root.TrimEnd(Path.DirectorySeparatorChar).StartsWith(app+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Root.TrimEnd(Path.DirectorySeparatorChar),app,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Архив ПТО должен находиться вне папки приложения.");
        Directory.CreateDirectory(Path.Combine(Root,"files"));
        Directory.CreateDirectory(Path.Combine(Root,"templates"));
        Directory.CreateDirectory(Path.Combine(Root,"exports"));
    }
    public string FilePath(string relative)
    {
        if(string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') ||
           relative.Contains(':') || Path.IsPathRooted(relative) ||
           relative.Split('/').Any(part=>part is "" or "." or ".."))
            throw new InvalidDataException("Недопустимый относительный путь архивного файла.");
        var segment=relative.Split('/')[0];
        if(segment is not ("files" or "templates" or "exports"))
            throw new InvalidDataException("Архивные файлы разрешены только в files/templates/exports.");
        var baseDir=Root.TrimEnd(Path.DirectorySeparatorChar);
        var full=Path.GetFullPath(Path.Combine(baseDir,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(baseDir+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл находится за границей архива.");
        return full;
    }
    public static string Sha(string path)
    {
        using var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static int PdfPages(string path)
    {
        if(!Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Для проверки PDF требуется исходный PDF.");
        try
        {
            using var document=PdfReader.Open(path,PdfDocumentOpenMode.Import);
            if(document.PageCount==0)throw new InvalidDataException("PDF не содержит страниц.");
            return document.PageCount;
        }
        catch(Exception ex) when(ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                "PDF повреждён или зашифрован; нужна корректная незашифрованная копия: "+
                Path.GetFileName(path),ex);
        }
    }
    private static void SafeCopy(string source,string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp=target+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var input=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.CopyTo(output);output.Flush(true);}
            File.Move(tmp,target,overwrite:false);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }

    public PtoAttachment AddAttachment(PtoPackage package,string source,
        string title,string kind,int? pages=null,IEnumerable<string>? systems=null)
    {
        var extension=Path.GetExtension(source).ToLowerInvariant();
        if(extension is not (".pdf" or ".xlsx"))
            throw new ArgumentException("Для приложений разрешены только PDF и XLSX.");
        if(!File.Exists(source))throw new FileNotFoundException("Не найдено приложение.",source);
        if(pages is <1)throw new ArgumentException("Количество листов должно быть положительным.");
        var sha=Sha(source);
        if(package.Attachments.Any(a=>a.Sha256.Equals(sha,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Этот файл уже добавлен в комплект.");
        var attachment=new PtoAttachment
        {
            Title=string.IsNullOrWhiteSpace(title)?Path.GetFileName(source):title.Trim(),
            Kind=kind,Pages=pages,Order=package.Attachments.Count,
            Systems=systems?.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToList()??new(),
            Sha256=sha
        };
        attachment.RelativePath="files/"+attachment.Id.ToString("N")+extension;
        var target=FilePath(attachment.RelativePath);
        SafeCopy(source,target);
        try
        {
            if(Sha(target)!=sha)throw new IOException("Изменение исходного файла при копировании.");
            package.Attachments.Add(attachment);
            package.ManualReview=false;
            package.Status="Черновик";
            Sql.Save(package);
            return attachment;
        }
        catch(PtoCommitUncertainException)
        {
            // SQL may already reference the immutable original. Never delete it.
            throw;
        }
        catch
        {
            package.Attachments.Remove(attachment);
            if(File.Exists(target))File.Delete(target);
            throw;
        }
    }

    public PtoCertificate ImportCertificate(string source,PtoCertificate data)
    {
        if(!Path.GetExtension(source).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Подтверждающий документ должен быть PDF.");
        data.Id=Guid.NewGuid();data.Revision=0;
        data.Sha256=Sha(source);
        data.Pages=PdfPages(source); // no corrupt/encrypted PDFs in the library
        data.RelativePath="files/"+data.Id.ToString("N")+".pdf";
        var path=FilePath(data.RelativePath);
        SafeCopy(source,path);
        try
        {
            if(Sha(path)!=data.Sha256)throw new IOException("Хеш сертификата при копировании не совпал.");
            Sql.SaveCertificate(data);
            return data;
        }
        catch(PtoCommitUncertainException){throw;} // SQL may already reference this PDF.
        catch{File.Delete(path);throw;}
    }

    public PtoAttachment LinkCertificate(PtoPackage package,PtoCertificate certificate)
    {
        if(package.Attachments.Any(a=>a.Sha256.Equals(certificate.Sha256,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Документ уже включён в этот акт.");
        var item=new PtoAttachment
        {
            Title=PtoRules.CertificateTitle(certificate),Kind=certificate.Kind,
            RelativePath=certificate.RelativePath,Sha256=certificate.Sha256,
            Pages=certificate.Pages,Order=package.Attachments.Count
        };
        // Metadata is intentionally an independent snapshot of the selected library record.
        var snapshot=JsonSerializer.Deserialize<PtoCertificate>(
            JsonSerializer.Serialize(certificate,PtoRules.Json),PtoRules.Json)!;
        package.Attachments.Add(item);
        package.CertificateSnapshots[item.Id]=snapshot;
        package.ManualReview=false;package.Status="Черновик";
        try{Sql.Save(package);return item;}
        catch(PtoCommitUncertainException){throw;}
        catch{package.Attachments.Remove(item);package.CertificateSnapshots.Remove(item.Id);throw;}
    }

    public (bool Exists,bool HashMatches,int? PdfPages) FileInfo(PtoAttachment a)
    {
        var path=FilePath(a.RelativePath);
        if(!File.Exists(path))return(false,false,null);
        var hash=Sha(path).Equals(a.Sha256,StringComparison.OrdinalIgnoreCase);
        if(!hash)return(true,false,null);
        var pages=Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)
            ?PdfPages(path):(int?)null;
        return(true,hash,pages);
    }
    public PtoRules.Findings Inspect(PtoPackage p)
    {
        var standard=PtoRules.Check(p);
        var errors=standard.Errors.ToList();
        var warnings=standard.Warnings.ToList();
        foreach(var a in p.Attachments.Where(x=>x.Included))
        {
            try
            {
                var state=FileInfo(a);
                if(!state.Exists)errors.Add("Файл не найден: "+a.Title);
                else if(!state.HashMatches)errors.Add("SHA-256 не совпадает: "+a.Title);
                if(state.PdfPages.HasValue && state.PdfPages!=a.Pages)
                    errors.Add($"Для «{a.Title}» указано {a.Pages} л., в PDF {state.PdfPages} стр.");
                if(a.Pages is null or <1)errors.Add("Не указано количество листов: "+a.Title);
                if(PtoRules.QualityKinds.Contains(a.Kind) &&
                   !Path.GetExtension(a.RelativePath).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
                    errors.Add("Документ о качестве должен быть PDF: "+a.Title);
            }
            catch(Exception ex) {errors.Add(a.Title+": "+ex.Message);}
        }
        var number=PtoRules.NumberKey(PtoRules.Text(p,"number"));
        if(number.Length>0 && Sql.Packages(p.ObjectId).Any(x=>x.Id!=p.Id &&
           PtoRules.NumberKey(PtoRules.Text(x,"number"))==number))
            errors.Add("Повторяется номер АОСР в объекте.");
        return new(errors,warnings);
    }

    private static string SafeName(string? value)
    {
        var invalid=Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).ToHashSet();
        var name=new string((value??"").Select(ch=>invalid.Contains(ch)||char.IsControl(ch)?'_':ch).ToArray())
            .Trim(' ','.');
        return name.Length==0?"Документ":name[..Math.Min(95,name.Length)];
    }

    public static void MergeQuality(PtoPackage data,string destination,Func<PtoAttachment,string> locate)
    {
        var items=data.Attachments.Where(a=>a.Included && PtoRules.QualityKinds.Contains(a.Kind))
            .OrderBy(a=>a.Order).ToArray();
        if(items.Length==0)return;
        using var result=new PdfDocument();
        result.Info.Title="Документы о качестве к АОСР № "+PtoRules.Text(data,"number");
        result.Info.Subject="Объединение копий: цифровые подписи исходных PDF не сохраняются.";
        foreach(var item in items)
        {
            var path=locate(item);
            if(!Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Документ о качестве не PDF: "+item.Title);
            using var input=PdfReader.Open(path,PdfDocumentOpenMode.Import);
            if(input.PageCount==0||input.PageCount!=item.Pages)
                throw new InvalidDataException(
                    $"Неверное количество страниц PDF «{item.Title}»: {input.PageCount} вместо {item.Pages}.");
            PdfSharp.Pdf.PdfPage? first=null;
            foreach(var page in input.Pages)
            {
                var added=result.AddPage(page);
                first??=added;
            }
            if(first is not null)result.Outlines.Add(item.Title,first);
        }
        result.Save(destination);
    }

    public sealed record Delivery(string Folder,string ManifestHash,IReadOnlyList<string> Files);
    public Delivery BuildDelivery(PtoPackage p)
    {
        var results=Inspect(p);
        if(results.Errors.Count>0)
            throw new InvalidDataException("Комплект не готов:\n"+string.Join("\n",results.Errors));
        if(!p.ManualReview)
            throw new InvalidOperationException("Подтвердите ручную проверку комплекта.");
        // Re-read before release: never quietly export a stale draft.
        var latest=Sql.Get(p.Id,p.ObjectId);
        if(latest.Revision!=p.Revision)
            throw new InvalidOperationException("Комплект изменился на сервере. Перечитайте перед выпуском.");
        var id=p.Id.ToString("N");
        var name=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+"-"+Guid.NewGuid().ToString("N")[..8];
        var relative="exports/"+id+"/"+name;
        var published=FilePath(relative);
        var stage=FilePath("exports/"+id+"/.delivery-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        Directory.CreateDirectory(stage);
        var files=new List<object>();
        var names=new List<string>();
        void Note(string path,int? pages=null,string? role=null)
        {
            var filename=Path.GetFileName(path);
            files.Add(new {file=filename,sha256=Sha(path),pages,role});
            names.Add(filename);
        }
        try
        {
            var output=PtoExcel.Generate(p,stage,CustomTemplates());
            Note(output.Aosr,role:"АОСР");Note(output.Registry,role:"Реестр");
            Note(output.SchemesRegistry,role:"Перечень исполнительных схем");
            Note(output.CertificatesRegistry,role:"Реестр сертификатов и паспортов");
            Note(output.DeliveryChecklist,role:"Перечень документов для сдачи");
            if(output.QualityRegistry is not null)Note(output.QualityRegistry,role:"Реестр качества");
            var serial=1;
            var verifiedCopies=new Dictionary<Guid,string>();
            foreach(var a in p.Attachments.Where(x=>x.Included).OrderBy(x=>x.Order))
            {
                var source=FilePath(a.RelativePath);
                var destination=Path.Combine(stage,$"{serial++:00}_"+SafeName(a.Title)+Path.GetExtension(source));
                SafeCopy(source,destination);
                if(Sha(destination)!=a.Sha256)
                    throw new InvalidDataException("Исходное приложение изменилось во время сборки: "+a.Title);
                verifiedCopies.Add(a.Id,destination);
                Note(destination,a.Pages,a.Kind);
            }
            if(p.Attachments.Any(a=>a.Included && PtoRules.QualityKinds.Contains(a.Kind)))
            {
                var qualityPath=Path.Combine(stage,"Документы_о_качестве.pdf");
                MergeQuality(p,qualityPath,a=>verifiedCopies[a.Id]);
                Note(qualityPath,role:"Объединённый PDF (подписи оригиналов не сохраняются)");
            }
            var manifest=Path.Combine(stage,"Состав_выпуска.json");
            File.WriteAllText(manifest,JsonSerializer.Serialize(new
            {
                act=PtoRules.Text(p,"number"),packageId=p.Id,revision=p.Revision,
                manualReview=p.ManualReview,
                caution="Проверка полномочий подписантов и применимости сертификатов выполнена вручную пользователем. "+
                        "Электронные подписи исходных PDF в объединённом файле не сохраняются.",
                snapshot=p,warnings=results.Warnings,files
            },new JsonSerializerOptions(PtoRules.Json){WriteIndented=true}),System.Text.Encoding.UTF8);
            var hash=Sha(manifest);
            Directory.Move(stage,published);
            try
            {
                Sql.RegisterRelease(new PtoReleaseRecord(Guid.NewGuid(),p.Id,p.ObjectId,relative,hash,DateTime.UtcNow),p.Revision);
            }
            catch(PtoCommitUncertainException ex)
            {
                throw new InvalidOperationException(ex.Message+"\nПапка сохранена: "+published,ex);
            }
            catch
            {
                if(Directory.Exists(published))Directory.Delete(published,true);
                throw;
            }
            return new Delivery(published,hash,names);
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }

    public Dictionary<string,string> CustomTemplates()
    {
        var result=new Dictionary<string,string>();
        foreach(var key in new[]{"aosr","registry"})
        {
            var path=FilePath("templates/"+key+".xlsx");
            if(File.Exists(path))result[key]=path;
        }
        return result;
    }
    public void StoreTemplate(string kind,string source)
    {
        if(kind is not ("aosr" or "registry"))
            throw new ArgumentException("Неизвестный шаблон.");
        PtoExcel.ValidateCustom(kind,source);
        var target=FilePath("templates/"+kind+".xlsx");
        if(File.Exists(target))throw new InvalidOperationException(
            "Пользовательский шаблон уже существует. Сначала сохраните резервную копию и удалите прежний файл.");
        var staged=target+".temp-"+Guid.NewGuid().ToString("N")+".xlsx";
        try
        {
            PtoExcel.CleanCustomTemplate(kind,source,staged);
            File.Move(staged,target,overwrite:false);
        }
        finally { if(File.Exists(staged))File.Delete(staged); }
    }
}
