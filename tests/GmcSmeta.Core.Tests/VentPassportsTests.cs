using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Offline behavioral parity with Python test_passports.py; never contacts a database.</summary>
public sealed class VentPassportsTests
{
    [Theory]
    [InlineData("П1","Система приточной вентиляции")]
    [InlineData("п11","Система приточной вентиляции")]
    [InlineData("В12","Система вытяжной вентиляции")]
    [InlineData("ДП1","Система подпора")]
    [InlineData("ПД2","Система подпора")]
    [InlineData("ДВ1","Система дымоудаления")]
    [InlineData("ДУ1","Система дымоудаления")]
    [InlineData("ВД3","Система дымоудаления")]
    [InlineData("Неизвестно","")]
    public void DetectsPurpose(string system,string expected) =>
        Assert.Equal(expected,VentPassports.DetectPurpose(system));

    [Theory]
    [InlineData("20",20)]
    [InlineData("23,25",23.25)]
    [InlineData("23.25",23.25)]
    [InlineData("0",0)]
    public void ParsesNumbers(string text,double expected) =>
        Assert.Equal((decimal)expected,VentPassports.Flow(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("nan")]
    [InlineData("NaN")]
    [InlineData("inf")]
    [InlineData("Infinity")]
    [InlineData("1e900")]
    [InlineData("1 20")]
    [InlineData("абв")]
    public void RejectsInvalidOrNonFiniteNumbers(string value) =>
        Assert.Throws<ArgumentException>(()=>VentPassports.Flow(value));

    [Fact]
    public void MissingMeasurementNeverCreatesActualTotal()
    {
        var p=New();
        p.Points.Add(new VentFlowPoint { Project="20",Actual="21" });
        p.Points.Add(new VentFlowPoint { Project="45",Actual="" });
        var (project,actual)=VentPassports.Totals(p.Points);
        Assert.Equal(65,project);
        Assert.Null(actual);
        Assert.True(VentPassports.IsDraft(p));
    }

    [Fact]
    public void CompleteMeasurementsGiveIndependentTotals()
    {
        var p=New();
        p.Points.Add(new VentFlowPoint { Project="20",Actual="21" });
        p.Points.Add(new VentFlowPoint { Project="45",Actual="44" });
        var totals=VentPassports.Totals(p.Points);
        Assert.Equal(65,totals.Project);
        Assert.Equal(65,totals.Actual);
        Assert.False(VentPassports.IsDraft(p));
    }

    [Fact]
    public void EmptyPointsAreDraft()
    {
        var p=New();
        Assert.True(VentPassports.IsDraft(p));
        Assert.Null(VentPassports.Totals(p.Points).Actual);
    }

    [Fact]
    public void AllEightSectionsHaveExactPythonLengths()
    {
        var p=New();
        Assert.Equal(8,p.Equipment.Count);
        Assert.Equal(new[] {7,8,7,9,6,12,5,6},
            p.Equipment.Select(x=>x.Project.Count));
        for(int i=0;i<8;i++)
            Assert.Equal(p.Equipment[i].Project.Count,p.Equipment[i].Actual.Count);
        VentPassports.Validate(p);
    }

    [Fact]
    public void TemplateFlowIsNeverInheritedButOtherEquipmentIs()
    {
        var p=New();
        p.Equipment[0].Project[0]="VTS 700";
        p.Equipment[0].Actual[0]="VTS 701";
        p.Equipment[0].Project[3]="2500";
        p.Equipment[0].Actual[3]="2510";
        p.Equipment[1].Project[1]="7,5";
        var template=VentPassports.MakeTemplate(p);
        Assert.Equal("",template.Equipment[0].Project[3]);
        Assert.Equal("",template.Equipment[0].Actual[3]);
        Assert.Equal("2500",p.Equipment[0].Project[3]);
        Assert.Equal("2510",p.Equipment[0].Actual[3]);
        var target=New();
        VentPassports.ApplyTemplate(target,template);
        Assert.Equal("7,5",target.Equipment[1].Project[1]);
        target.Equipment[1].Project[1]="99";
        Assert.Equal("7,5",template.Equipment[1].Project[1]);
    }

    [Fact]
    public void CopyClearsOnlyActualPointMeasurements()
    {
        var p=New();
        p.System="П1";
        p.Points.Add(new VentFlowPoint {Section="1",Room="Кабинет",
            Project="80",Actual="79"});
        var copy=VentPassports.Duplicate(p);
        Assert.NotEqual(copy.Id,p.Id);
        Assert.Equal("П1 (копия)",copy.System);
        Assert.Equal("80",copy.Points[0].Project);
        Assert.Equal("",copy.Points[0].Actual);
        Assert.Equal("79",p.Points[0].Actual);
        Assert.True(VentPassports.IsDraft(copy));
    }

    [Fact]
    public void BatchCopiesCommonRequisitesAndEquipmentButNotMeasurements()
    {
        var p=New();
        p.Organization="Наладочная организация";
        p.Equipment[1].Project[0]="Эл. двигатель";
        p.Points.Add(new VentFlowPoint {Project="20",Actual="20"});
        var batch=VentPassports.BatchCreate(p,"П1\nВ2\n\nДП3\n");
        Assert.Equal(3,batch.Count);
        Assert.Equal("Система подпора",batch[2].Purpose);
        Assert.All(batch,x=>{
            Assert.Equal(p.ObjectId,x.ObjectId);
            Assert.Equal(p.Organization,x.Organization);
            Assert.Empty(x.Points);
            Assert.Equal("Эл. двигатель",x.Equipment[1].Project[0]);
        });
        batch[0].Equipment[1].Project[0]="Изменено";
        Assert.NotEqual(batch[0].Equipment[1].Project[0],batch[1].Equipment[1].Project[0]);
    }

    [Fact]
    public void ApplyCommonDoesNotOverwriteSystemOrMeasurements()
    {
        var p=New();
        p.System="П1";
        p.Object="Новый объект";
        p.Date="21.11.2026";
        var other=New();
        other.System="В2";
        other.Points.Add(new VentFlowPoint {Project="100",Actual="90"});
        VentPassports.ApplyCommon(p,new[]{p,other});
        Assert.Equal("Новый объект",other.Object);
        Assert.Equal("21.11.2026",other.Date);
        Assert.Equal("В2",other.System);
        Assert.Equal("90",other.Points[0].Actual);
    }

    [Fact]
    public void ValidateChecksActualColumnEvenIfProjectIsBlank()
    {
        var p=New();
        p.Points.Add(new VentFlowPoint {Project="",Actual="-8"});
        Assert.Throws<ArgumentException>(()=>VentPassports.Validate(p));
    }

    [Fact]
    public void ValidationRejectsMissingEquipmentAndCrossObjectIdentity()
    {
        var p=New();
        p.Equipment.RemoveAt(0);
        Assert.Throws<ArgumentException>(()=>VentPassports.Validate(p));
        p=New();
        p.ObjectId=0;
        Assert.Throws<ArgumentException>(()=>VentPassports.Validate(p));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmeta;Integrated Security=true")]
    public void SqlCannotMigrateProduction(string connection) =>
        Assert.Throws<InvalidOperationException>(()=>new SqlServerPassports(connection));

    private static VentPassport New()=>
        VentPassports.Create(41,"Объект для теста","Улица, 1");
}
