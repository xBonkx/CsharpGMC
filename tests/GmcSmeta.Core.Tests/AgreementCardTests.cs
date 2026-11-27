using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AgreementCardTests
{
    private static SqlServerKsRequisites.Values Print() =>
        new(12, "Договор", "Заказчик", "Стройка", "Организация", "Директор", "Иванов",
            "", "", "", "", "Монтаж", "27.11.2026", "");

    [Fact]
    public void SharedFieldsValidateWithoutAnAnnex()
    {
        SqlServerWorkspace.ValidateSharedPrint(12, Print());
        SqlServerWorkspace.ValidateSharedPrint(12, Print() with { WorksStart = "" });
    }

    [Fact]
    public void RejectsWrongCardLocalSitePartAndInvalidDate()
    {
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.ValidateSharedPrint(13, Print()));
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.ValidateSharedPrint(12, Print() with { SitePart = "Корпус 1" }));
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.ValidateSharedPrint(12, Print() with { WorksStart = "31.02.2026" }));
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.ValidateSharedPrint(12, Print() with { CustomerFull = new string('a', 2001) }));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Cutover_Test;Integrated Security=true")]
    public void SaveNeverOpensNonPilotDatabase(string connection) =>
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.SaveAgreementCard(
            connection, 12, new ObjectProperties("Договор"), Print(), "revision"));

    [Fact]
    public void EmbeddedPageAndKsReadTheSameSharedObjectFields()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "AgreementCardView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var db = Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db");
        var flow = File.ReadAllText(Path.Combine(app, "MainWindow.SqlServer.cs"));
        Assert.Contains("OpenAgreementCard(target);", flow);
        var view = File.ReadAllText(Path.Combine(app, "MainWindow.AgreementCard.cs"));
        Assert.Contains("ShowPage(page);", view);
        Assert.DoesNotContain("ShowDialog", view);
        Assert.Contains("EditSqlServerContractProperties(doc, makeContract: false);", view);
        var markup = File.ReadAllText(Path.Combine(app, "AgreementCardView.xaml"));
        Assert.Contains("Название объекта (в дереве)", markup);
        Assert.Contains("x:Name=\"AgreementNumberBox\" IsReadOnly=\"True\"", markup);
        Assert.Contains("x:Name=\"AgreementDateBox\" IsReadOnly=\"True\"", markup);
        Assert.Contains("x:Name=\"CompanyBox\" IsReadOnly=\"True\"", markup);
        Assert.Contains("x:Name=\"SourceGrid\"", markup);
        var write = File.ReadAllText(Path.Combine(db, "SqlServerWorkspace.AgreementCard.cs"));
        Assert.Contains("CheckRevision(conn, tx, expectedRevision);", write);
        Assert.Contains("UPDATE dbo.objects SET", write);
        Assert.DoesNotContain("UPDATE dbo.contracts", write);
        var read = File.ReadAllText(Path.Combine(db, "SqlServerKsRequisites.cs"));
        Assert.Contains("FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id", read);
        Assert.Contains("customer_full,site_full,cust_org,cust_post,cust_name", read);
    }
}
