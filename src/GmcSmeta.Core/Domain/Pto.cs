using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmcSmeta.Core.Domain;

/// <summary>Equivalent of smeta/pto/model.py, independent from SQL and the live estimate.</summary>
public static class PtoRules
{
    public static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["number"]="Номер акта", ["date"]="Дата акта (ДД.ММ.ГГГГ)",
        ["section"]="Раздел работ", ["object"]="Объект строительства",
        ["address"]="Адрес / строительные оси",
        ["customer"]="Застройщик / технический заказчик — реквизиты",
        ["contractor"]="Лицо, осуществляющее строительство — реквизиты",
        ["designer"]="Проектировщик — реквизиты",
        ["customer_rep"]="Представитель заказчика, полномочия",
        ["contractor_rep"]="Представитель подрядчика, полномочия",
        ["control_rep"]="Строительный контроль подрядчика, полномочия",
        ["designer_rep"]="Представитель проектировщика, полномочия",
        ["worker_rep"]="Представитель исполнителя, полномочия",
        ["executor"]="Исполнитель работ",
        ["works"]="Наименование и объёмы скрытых работ",
        ["start"]="Начало работ (ДД.ММ.ГГГГ)",
        ["finish"]="Окончание работ (ДД.ММ.ГГГГ)",
        ["project"]="Рабочая / проектная документация",
        ["materials"]="Материалы и документы о качестве",
        ["evidence"]="Исполнительные схемы / подтверждение работ",
        ["standards"]="Нормативные документы",
        ["next_works"]="Последующие работы",
        ["extra"]="Дополнительные сведения",
        ["copies"]="Количество экземпляров акта",
        ["other_rep"]="Представители иных лиц",
        ["handed_by"]="Передал (для реестра)",
        ["accepted_by"]="Принял (для реестра)",
        ["notes"]="Замечания пользователя"
    };
    public static readonly string[] Statuses = ["Черновик","На проверке","Готов","Подписан"];
    public static readonly string[] QualityKinds =
        ["Сертификат","Декларация","Паспорт","Отказное письмо","Решение об отказе в сертификации"];
    public static readonly string[] Required =
        ["number","date","object","customer","contractor","executor","works","start","finish","project",
         "materials","evidence","standards","next_works","customer_rep","contractor_rep","control_rep",
         "worker_rep","copies"];
    public static JsonSerializerOptions Json { get; } = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
    public static string Text(PtoPackage p, string field) =>
        p.Fields.TryGetValue(field, out var value) ? value ?? "" : "";

    public static PtoPackage New(long objectId,string objectName,string address,
        long? contractId=null,string contractLabel="",string contractNumber="",string contractDate="") =>
        new()
        {
            ObjectId=objectId,ContractId=contractId,ContractLabel=contractLabel,
            ContractNumber=contractNumber,ContractDate=contractDate,
            Fields=Fields.Keys.ToDictionary(k=>k,k=>k switch {
                "object"=>objectName,"address"=>address,"copies"=>"1",_=>""},StringComparer.Ordinal)
        };

    public static PtoPackage Duplicate(PtoPackage old, string newNumber)
    {
        var p=JsonSerializer.Deserialize<PtoPackage>(JsonSerializer.Serialize(old,Json),Json)!;
        p.Id=Guid.NewGuid();p.Revision=0;p.Status="Черновик";
        p.ManualReview=false;p.CopiedFrom=old.Id;
        p.Fields["number"]=newNumber;
        foreach(var key in new[]{"date","start","finish","works","evidence","notes"})
            p.Fields[key]="";
        p.Attachments.Clear();
        p.CertificateSnapshots.Clear();
        p.SelectedWorkSystems.Clear();
        p.Positions.RemoveAll(x=>x.AttachmentId.HasValue);
        p.LastRelease="";
        return p;
    }

    public static string NumberKey(string number) => (number??"").Trim().ToUpperInvariant();

    public static string NextNumber(IEnumerable<PtoPackage> packages,PtoPackage active)
    {
        var current=Text(active,"number");
        var digits=current.Length;
        while(digits>0 && char.IsAsciiDigit(current[digits-1]))digits--;
        var prefix=digits<current.Length?current[..digits]:
            (Text(active,"section").Trim() is var s && s.Length>0?s:"АОСР")+"-";
        int max=0;
        foreach(var p in packages.Where(p=>p.ObjectId==active.ObjectId))
        {
            var number=Text(p,"number");
            if (!number.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
            var suffix=number[prefix.Length..];
            if(int.TryParse(suffix,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed) && parsed>max)
                max=parsed;
        }
        return prefix+(max+1).ToString(CultureInfo.InvariantCulture);
    }

    public static DateTime? Date(string value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        if(!DateTime.TryParseExact(value.Trim(),"dd.MM.yyyy",CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var date))
            throw new ArgumentException("Дата должна быть в формате ДД.ММ.ГГГГ: "+value);
        return date;
    }

    public static void ValidateShape(PtoPackage p)
    {
        if(p.ObjectId<=0)throw new ArgumentException("Объект ПТО не выбран.");
        if(!Statuses.Contains(p.Status))throw new ArgumentException("Неизвестный статус ПТО.");
        if(p.Fields is null || p.Fields.Values.Any(v=>v is null || v.Length>16000))
            throw new ArgumentException("Неправильные или слишком длинные поля ПТО.");
        if(p.Attachments.Count>2000 || p.Positions.Count>50000)
            throw new ArgumentException("Слишком много вложений или работ.");
        foreach(var a in p.Attachments)
        {
            if(a.Title.Length is 0 or >500 || a.Kind.Length>100 ||
               a.Pages is <1 || a.Sha256.Length!=64 || !a.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Некорректное приложение: "+a.Title);
        }
        if(p.Attachments.Select(a=>a.Id).Distinct().Count()!=p.Attachments.Count)
            throw new ArgumentException("Дубли ID приложений.");
    }

    public sealed record Findings(IReadOnlyList<string> Errors,IReadOnlyList<string> Warnings)
    {
        public bool Ready => Errors.Count==0;
    }

    /// <summary>Never guesses field values or claims suitability of certificates.</summary>
    public static Findings Check(PtoPackage p, bool checkFileData=false,
        Func<PtoAttachment,(bool Exists,bool HashMatches,int? PdfPages)>? fileInfo=null)
    {
        var errors=new List<string>();
        var warnings=new List<string>();
        foreach(var key in Required)
            if(string.IsNullOrWhiteSpace(Text(p,key)))
                errors.Add("Не заполнено: "+Fields[key]);
        var dates=new Dictionary<string,DateTime>();
        foreach(var key in new[]{"date","start","finish"})
            try
            {
                if(Date(Text(p,key)) is { } date)dates[key]=date;
            }
            catch(ArgumentException){errors.Add("Неверная дата: "+Fields[key]);}
        if(dates.TryGetValue("start",out var start) && dates.TryGetValue("finish",out var finish) &&
           start>finish)errors.Add("Начало работ позже окончания.");
        if(dates.TryGetValue("date",out var actDate) && dates.TryGetValue("finish",out var end) &&
           end>actDate)errors.Add("Дата акта раньше окончания работ.");
        if(!int.TryParse(Text(p,"copies"),out var copies) || copies<1)
            errors.Add("Неверное количество экземпляров.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected=p.Attachments.Where(a=>a.Included).OrderBy(a=>a.Order).ToArray();
        foreach(var a in selected)
        {
            if(!seen.Add(a.Sha256))
                errors.Add("Дублирование приложения: "+a.Title);
            if(a.Pages is null or <1)
                (checkFileData?errors:warnings).Add("Укажите количество листов: "+a.Title);
            if(checkFileData && fileInfo is not null)
            {
                var state=fileInfo(a);
                if(!state.Exists)errors.Add("Не найден файл: "+a.Title);
                else if(!state.HashMatches)errors.Add("Файл изменён после добавления: "+a.Title);
                if(a.Kind is not null && QualityKinds.Contains(a.Kind) && state.PdfPages is not null &&
                   state.PdfPages!=a.Pages)
                    errors.Add("В PDF "+state.PdfPages+" стр., в карточке "+a.Pages+": "+a.Title);
            }
            if(QualityKinds.Contains(a.Kind))
            {
                if(!p.CertificateSnapshots.TryGetValue(a.Id,out var cert))
                    warnings.Add("Не заполнена карточка подтверждающего документа: "+a.Title);
                else
                {
                    if(!cert.Verified)warnings.Add("Реквизиты не подтверждены: "+a.Title);
                    if(string.IsNullOrWhiteSpace(cert.Number) || string.IsNullOrWhiteSpace(cert.Issued))
                        warnings.Add("Нужна ручная проверка номера и даты: "+a.Title);
                    try
                    {
                        if(!cert.NoExpiry && Date(cert.Expires) is { } expires)
                        {
                            if(expires<DateTime.Today)warnings.Add("Срок действия истёк: "+a.Title);
                            if(dates.TryGetValue("start",out var began) && expires<began)
                                warnings.Add("Сертификат мог истечь до начала работ: "+a.Title);
                        }
                    }
                    catch(ArgumentException){errors.Add("Неверная дата сертификата: "+a.Title);}
                }
            }
        }
        if(selected.Length==0)warnings.Add("Нет приложений: подтверждение работ и материалов не проверено.");
        var systems=selected.Where(a=>a.Kind=="Исполнительная схема")
            .SelectMany(a=>a.Systems).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var system in p.SelectedWorkSystems.Distinct(StringComparer.OrdinalIgnoreCase).Except(
            systems,StringComparer.OrdinalIgnoreCase))
            warnings.Add("Система работ не сопоставлена со схемой: "+system);
        warnings.Add("Полномочия подписантов, применимость сертификатов и соответствие ВОР схемам требуют ручной проверки.");
        return new Findings(errors,warnings);
    }

    public static string CertificateTitle(PtoCertificate c)=>
        c.Kind+(c.Number.Length==0?"":" № "+c.Number)+(c.Issued.Length==0?"":" от "+c.Issued)
        +(c.Product.Length==0?"":" — "+c.Product);
}

public sealed class PtoPackage
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public long ObjectId {get;set;}
    public long? ContractId {get;set;}
    public string ContractLabel {get;set;}="";
    public string ContractNumber {get;set;}="";
    public string ContractDate {get;set;}="";
    public string Status {get;set;}="Черновик";
    public bool ManualReview {get;set;}
    public Dictionary<string,string> Fields {get;set;}=new(StringComparer.Ordinal);
    public List<PtoWork> Positions {get;set;}=new();
    public List<string> SelectedWorkSystems {get;set;}=new();
    public List<PtoAttachment> Attachments {get;set;}=new();
    public Dictionary<Guid,PtoCertificate> CertificateSnapshots {get;set;}=new();
    public Guid? CopiedFrom {get;set;}
    public string LastRelease {get;set;}="";
    [JsonIgnore] public long Revision {get;set;}
}

public sealed class PtoWork
{
    public string System {get;set;}="";
    public string Name {get;set;}="";
    public string Unit {get;set;}="";
    public string Qty {get;set;}="";
    public Guid? AttachmentId {get;set;}
    public long? SourceActId {get;set;}
    public long? SourcePositionId {get;set;}
    public string SourceFile {get;set;}="";
    public string SourceSheet {get;set;}="";
    public int SourceRow {get;set;}
}
public sealed class PtoAttachment
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Title {get;set;}="";
    public string Kind {get;set;}="";
    public string RelativePath {get;set;}="";
    public int? Pages {get;set;}
    public bool Included {get;set;}=true;
    public string Sha256 {get;set;}="";
    public int Order {get;set;}
    public List<string> Systems {get;set;}=new();
}
public sealed class PtoCertificate
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Kind {get;set;}="Сертификат";
    public string Product {get;set;}="";
    public string Manufacturer {get;set;}="";
    public string Number {get;set;}="";
    public string Issued {get;set;}="";
    public string Expires {get;set;}="";
    public bool NoExpiry {get;set;}
    public bool Verified {get;set;}
    public string Materials {get;set;}="";
    public string RelativePath {get;set;}="";
    public string Sha256 {get;set;}="";
    public int? Pages {get;set;}
    [JsonIgnore]public long Revision {get;set;}
}

public sealed record PtoReleaseRecord(Guid Id,Guid PackageId,long ObjectId,
    string RelativePath,string ManifestSha256,DateTime CreatedUtc);
