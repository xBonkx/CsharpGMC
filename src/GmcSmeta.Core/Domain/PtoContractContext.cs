namespace GmcSmeta.Core.Domain;

/// <summary>A PTO work source remains an estimate document, while its legal
/// requisites belong to the independently linked agreement on schema 32.</summary>
public sealed record PtoContractContext(long Id,long ObjectId,string Number,string Date,
    string Name,string Kind,string Contractor)
{
    public sealed record Legal(long Id,string Number,string Date,string Customer,string Contractor);
    public bool LegalRequired {get;init;}
    public Legal? Agreement {get;init;}
    public string AnnexNumber {get;init;}="";

    public override string ToString()
    {
        if(!LegalRequired)
            return (Kind=="dop"?"ДС ":"Договор ")+"№"+Number+
                (string.IsNullOrWhiteSpace(Name)?"":" · "+Name);

        var sourceNumber=Kind=="dop"?Number:AnnexNumber;
        var source=(Kind=="dop"?"ДС":"Приложение")+
            (string.IsNullOrWhiteSpace(sourceNumber)?"":" №"+sourceNumber)+
            (string.IsNullOrWhiteSpace(Name)?"":" · "+Name);
        if(Agreement is not {Id:>0} legal)
            return source+" · договор не привязан";
        var caption=string.IsNullOrWhiteSpace(legal.Number)?"номер не заполнен":"№"+legal.Number;
        return source+" · договор "+caption+
            (string.IsNullOrWhiteSpace(legal.Date)?"":" от "+legal.Date);
    }

    public PtoPackage NewPackage(long objectId,string objectName,string address,string objectCustomer,
        IReadOnlyDictionary<string,string>? defaults=null)
    {
        if(ObjectId!=objectId)
            throw new InvalidOperationException("Приложение относится к другому объекту.");
        if(LegalRequired && Agreement is not {Id:>0})
            throw new InvalidOperationException("Приложение не привязано к действующему юридическому договору. Проверьте реквизиты перед созданием ПТО.");

        // Do not fill an intentionally blank legal number/date from a legacy
        // annex or DS. The existing document ID remains the work-source link.
        var p=PtoRules.New(objectId,objectName,address,Id,ToString(),
            LegalRequired?Agreement!.Number:Number,LegalRequired?Agreement!.Date:Date);
        p.Fields["customer"]=LegalRequired?Agreement!.Customer:objectCustomer;
        p.Fields["contractor"]=LegalRequired?Agreement!.Contractor:Contractor;
        p.Fields["executor"]=p.Fields["contractor"];
        ApplyDefaults(p,defaults,LegalRequired);
        return p;
    }

    public static void ApplyDefaults(PtoPackage package,IReadOnlyDictionary<string,string>? defaults,
        bool legalRequired=false)
    {
        if(defaults is null)return;
        foreach(var (key,value) in defaults)
        {
            // Object-level profiles are not proof of the parties of a
            // particular legal agreement. Leave incomplete parties for manual review.
            if(legalRequired && key is ("customer" or "contractor" or "executor"))continue;
            if(PtoRules.Fields.ContainsKey(key)&&string.IsNullOrWhiteSpace(PtoRules.Text(package,key)))
                package.Fields[key]=value;
        }
    }
}
