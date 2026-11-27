using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PtoRulesTests
{
    private static PtoPackage Sample()
    {
        var p=PtoRules.New(1,"Школа","Строительный проезд, 5");
        p.Fields["number"]="ОВ-7";p.Fields["date"]="21.11.2026";
        p.Fields["start"]="01.11.2026";p.Fields["finish"]="20.11.2026";
        p.Fields["works"]="Скрытые работы по системам ОВ";
        return p;
    }
    [Fact]public void CreatesObjectOnlyPackageWithSnapshot()
    {
        var p=Sample();
        Assert.Null(p.ContractId);
        Assert.Equal("Школа",PtoRules.Text(p,"object"));
        Assert.Equal("Строительный проезд, 5",PtoRules.Text(p,"address"));
        Assert.Equal("Черновик",p.Status);
        Assert.False(p.ManualReview);
    }
    [Theory]
    [InlineData("main","П-2")]
    [InlineData("dop","ДС-5")]
    public void NewPtoUsesLegalRequisitesButKeepsEstimateDocumentId(string kind,string ownNumber)
    {
        var source=new PtoContractContext(22,1,ownNumber,"01.02.2026","ОВ",kind,"Старый подрядчик")
        {
            LegalRequired=true,AnnexNumber=ownNumber,
            Agreement=new(7,"Д-42","23.01.2025","Новый заказчик","Новый подрядчик")
        };
        var p=source.NewPackage(1,"Стройка","Адрес","Старый заказчик");
        Assert.Equal(22L,p.ContractId);
        Assert.Equal("Д-42",p.ContractNumber);
        Assert.Equal("23.01.2025",p.ContractDate);
        Assert.Contains(ownNumber,p.ContractLabel);
        Assert.Contains("Д-42",p.ContractLabel);
        Assert.Equal("Стройка",PtoRules.Text(p,"object"));
        Assert.Equal("Новый заказчик",PtoRules.Text(p,"customer"));
        Assert.Equal("Новый подрядчик",PtoRules.Text(p,"contractor"));
        Assert.Equal("Новый подрядчик",PtoRules.Text(p,"executor"));
        Assert.Equal("",PtoRules.Text(p,"start"));
        Assert.Equal("",PtoRules.Text(p,"finish"));
    }

    [Fact]
    public void IncompleteLegalRequisitesAreNotFilledFromAnotherAgreementOrProfile()
    {
        var source=new PtoContractContext(22,1,"ДС-5","01.02.2026","ОВ","dop","Старый подрядчик")
        {LegalRequired=true,Agreement=new(7,"","","","")};
        var defaults=new Dictionary<string,string>
        {["customer"]="Другой заказчик",["contractor"]="Другой подрядчик",["executor"]="Другой исполнитель",["project"]="ОВ-1"};
        var p=source.NewPackage(1,"Стройка","Адрес","Старый заказчик",defaults);
        Assert.Equal("",p.ContractNumber);
        Assert.Equal("",p.ContractDate);
        Assert.Equal("",PtoRules.Text(p,"customer"));
        Assert.Equal("",PtoRules.Text(p,"contractor"));
        Assert.Equal("",PtoRules.Text(p,"executor"));
        Assert.Equal("ОВ-1",PtoRules.Text(p,"project"));
        Assert.Contains("номер не заполнен",p.ContractLabel);
        Assert.Contains(PtoRules.Check(p).Errors,e=>e.Contains(PtoRules.Fields["contractor"]));
    }

    [Fact]
    public void MissingLegalLinkAndWrongObjectCannotCreateNewPto()
    {
        var source=new PtoContractContext(22,1,"42","01.02.2026","ОВ","main","ГМС");
        Assert.Throws<InvalidOperationException>(()=>(source with {LegalRequired=true}).NewPackage(1,"Стройка","",""));
        Assert.Throws<InvalidOperationException>(()=>source.NewPackage(2,"Другая стройка","",""));
    }

    [Fact]
    public void LegacySourceKeepsOriginalRequisitesAndExistingSnapshotsStayUnchanged()
    {
        var source=new PtoContractContext(22,1,"42","01.02.2026","ОВ","main","ГМС");
        var old=source.NewPackage(1,"Стройка","Адрес","Заказчик");
        var updated=source with {LegalRequired=true,Agreement=new(7,"99","02.02.2026","Новый","Другой")};
        var fresh=updated.NewPackage(1,"Стройка","Адрес","Заказчик");
        Assert.Equal("42",old.ContractNumber);
        Assert.Equal("99",fresh.ContractNumber);
        Assert.Equal("ГМС",PtoRules.Text(old,"contractor"));
        Assert.Equal("Другой",PtoRules.Text(fresh,"contractor"));
    }

    [Fact]public void NumberingUsesObjectScope()
    {
        var p=Sample();
        var next=PtoRules.Duplicate(p,"ОВ-8");
        var elsewhere=PtoRules.Duplicate(p,"ОВ-99");
        elsewhere.ObjectId=2;
        Assert.Equal("ОВ-9",PtoRules.NextNumber(new[]{p,next,elsewhere},p));
        Assert.Equal("ОВ-100",PtoRules.NextNumber(new[]{elsewhere},elsewhere));
    }
    [Fact]public void NumberingUsesSectionWhenNoNumericSuffix()
    {
        var p=Sample();p.Fields["section"]="ОВ";p.Fields["number"]="Акт";
        Assert.Equal("ОВ-1",PtoRules.NextNumber(Array.Empty<PtoPackage>(),p));
    }
    [Fact]public void CopyClearsDatesWorksAndConfirmationButNotSnapshottedRequisites()
    {
        var p=Sample();
        p.Fields["customer"]="ООО Заказчик";
        p.ManualReview=true;p.Status="Готов";
        p.Attachments.Add(new PtoAttachment{Title="схема",Kind="Исполнительная схема",
            RelativePath="files/x.pdf",Pages=1,Sha256=new string('a',64)});
        p.SelectedWorkSystems.Add("П1");
        p.Positions.Add(new PtoWork{System="П1",Name="Каналы",Qty="10",AttachmentId=Guid.NewGuid()});
        p.Positions.Add(new PtoWork{System="П1",Name="Монтаж",Qty="2"});
        var copy=PtoRules.Duplicate(p,"ОВ-8");
        Assert.Equal("ОВ-8",PtoRules.Text(copy,"number"));
        Assert.Equal("ООО Заказчик",PtoRules.Text(copy,"customer"));
        foreach(var key in new[]{"date","start","finish","works","evidence","notes"})
            Assert.Equal("",PtoRules.Text(copy,key));
        Assert.Empty(copy.Attachments);
        Assert.Single(copy.Positions);
        Assert.Empty(copy.SelectedWorkSystems);
        Assert.False(copy.ManualReview);
        Assert.Equal("Черновик",copy.Status);
        Assert.Equal("ОВ-7",PtoRules.Text(p,"number"));
    }
    [Fact]public void MissingFieldsAreCriticalAndNoFakeDates()
    {
        var check=PtoRules.Check(Sample());
        Assert.Contains(check.Errors,e=>e.Contains(PtoRules.Fields["customer"]));
        Assert.Contains(check.Errors,e=>e.Contains(PtoRules.Fields["contractor"]));
        Assert.Contains(check.Warnings,w=>w.Contains("ручной проверки"));
    }
    [Fact]public void InvalidDatesAndChronologyBlockReadiness()
    {
        var p=Sample();p.Fields["date"]="01.11.2026";
        Assert.Contains(PtoRules.Check(p).Errors,e=>e.Contains("раньше окончания"));
        p.Fields["date"]="bad";
        Assert.Contains(PtoRules.Check(p).Errors,e=>e.Contains("Неверная дата"));
        p.Fields["date"]="21.11.2026";p.Fields["start"]="31.11.2026";
        Assert.Contains(PtoRules.Check(p).Errors,e=>e.Contains("Неверная дата"));
    }
    [Fact]public void DuplicateAttachmentHashesAreErrorsOnlyWhenIncluded()
    {
        var p=Sample();
        var one=new PtoAttachment{Title="one",Kind="Другое",Sha256=new string('a',64),Pages=1};
        var two=new PtoAttachment{Title="two",Kind="Другое",Sha256=new string('a',64),Pages=1};
        p.Attachments.Add(one);p.Attachments.Add(two);
        Assert.Contains(PtoRules.Check(p).Errors,e=>e.Contains("Дублирование"));
        two.Included=false;
        Assert.DoesNotContain(PtoRules.Check(p).Errors,e=>e.Contains("Дублирование"));
    }
    [Theory]
    [InlineData(5,false)]
    [InlineData(6,true)]
    public void CountsIncludedQualityOnly(int quality,bool registryRequired)
    {
        var p=Sample();
        for(int i=0;i<quality;i++)
            p.Attachments.Add(new PtoAttachment{Title="Сертификат "+i,Kind="Сертификат",
                Sha256=i.ToString("x64"),Pages=1});
        Assert.Equal(registryRequired,p.Attachments.Count(a=>a.Included &&
            PtoRules.QualityKinds.Contains(a.Kind))>5);
        p.Attachments[^1].Included=false;
        Assert.False(p.Attachments.Count(a=>a.Included&&PtoRules.QualityKinds.Contains(a.Kind))>5);
    }
    [Fact]public void UnverifiedCertificateNeverAutomaticallyApproved()
    {
        var p=Sample();
        var a=new PtoAttachment{Title="Паспорт",Kind="Паспорт",Sha256=new string('a',64),Pages=1};
        p.Attachments.Add(a);
        p.CertificateSnapshots[a.Id]=new PtoCertificate{Number="N1",Issued="10.01.2026",
            Expires="11.01.2026",Verified=false};
        var warnings=PtoRules.Check(p).Warnings;
        Assert.Contains(warnings,w=>w.Contains("не подтверждены"));
        Assert.Contains(warnings,w=>w.Contains("истёк"));
    }
    [Fact]public void SchemeSystemsAreWarningsNeverInferred()
    {
        var p=Sample();
        p.SelectedWorkSystems.Add("П1");
        var warnings=PtoRules.Check(p).Warnings;
        Assert.Contains(warnings,w=>w.Contains("П1"));
        p.Attachments.Add(new PtoAttachment{Kind="Исполнительная схема",Title="Схема",
            Sha256=new string('a',64),Pages=1,Systems=["П1"]});
        Assert.DoesNotContain(PtoRules.Check(p).Warnings,w=>w.Contains("не сопоставлена"));
    }
    [Fact]public void ShapeRejectsDuplicateFileIds()
    {
        var p=Sample();
        var id=Guid.NewGuid();
        p.Attachments.Add(new PtoAttachment{Id=id,Title="one",Sha256=new string('a',64)});
        p.Attachments.Add(new PtoAttachment{Id=id,Title="two",Sha256=new string('b',64)});
        Assert.Throws<ArgumentException>(()=>PtoRules.ValidateShape(p));
    }
    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmeta;Integrated Security=true")]
    public void SqlRejectsNonPilotBeforeConnecting(string connection)
    {
        Assert.Throws<InvalidOperationException>(()=>new SqlServerPto(connection));
        Assert.Throws<InvalidOperationException>(()=>SqlServerPtoBackup.Export(connection));
    }
}
