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
