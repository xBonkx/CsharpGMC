using System.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Pure SQL Server-only checks. No connection to any database is made.</summary>
public sealed class SqlServerWorkspaceTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 12)]
    [InlineData(true, 0)]
    [InlineData(true, 12)]
    [InlineData(true, int.MaxValue)]
    [InlineData(true, null)]
    public void CompanyReaderAcceptsIntAndBigIntOrder(bool bigInt, int? order)
    {
        using var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        foreach (var column in new[] { "name", "address", "inn_kpp", "phone", "email" })
            table.Columns.Add(column, typeof(string));
        table.Columns.Add("vat_rate", typeof(double));
        foreach (var column in new[] { "legal_full", "signer_post", "signer_name" })
            table.Columns.Add(column, typeof(string));
        table.Columns.Add("order", bigInt ? typeof(long) : typeof(int));
        table.Columns.Add("uses", typeof(long));
        table.Columns.Add("logo", typeof(byte[]));
        const long id = (long)int.MaxValue + 1;
        object orderValue = order is int n ? (bigInt ? (object)(long)n : n) : DBNull.Value;
        var logo = new byte[] { 1, 2, 3 };
        table.Rows.Add(id, "ООО Тест", "", "", "", "", 22d, "", "", "", orderValue, id, logo);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var company = SqlServerWorkspaceNavigation.ReadCompany(reader);
        Assert.Equal(id, company.Id);
        Assert.Equal(id, company.Uses);
        Assert.Equal(order ?? 0, company.Order);
        Assert.Equal(22m, company.VatRate);
        Assert.Equal("ООО Тест", company.Name);
        Assert.Equal(logo, company.Logo);
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmeta;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Production;Integrated Security=true")]
    public void Writes_RejectNonPilotCatalogs(string connectionString) =>
        Assert.Throws<InvalidOperationException>(
            () => SqlServerWorkspace.RequirePilotDatabase(connectionString));

    [Fact]
    public void Writes_AllowPilotCatalogWithoutConnecting()
    {
        SqlServerWorkspace.RequirePilotDatabase(
            "Server=localhost;Database=GmcSmetaPilot_UnitTest;Integrated Security=true");
    }

    [Fact]
    public void DescribeConnection_ShowsServerAndDatabaseWithoutSecrets()
    {
        var label = SqlServerWorkspace.DescribeConnection(
            "Server=SQL01\\PILOT;Database=GmcSmetaPilot_Demo;User Id=sa;Password=TOP_SECRET");
        Assert.Equal("SQL01\\PILOT · GmcSmetaPilot_Demo", label);
        Assert.DoesNotContain("TOP_SECRET", label);
        Assert.DoesNotContain("sa", label);

        Assert.Equal("SQL01\\PILOT",
            SqlServerWorkspace.DescribeConnection("Server=SQL01\\PILOT;Integrated Security=true"));
    }

    [Fact]
    public void ObjectValidation_StripsWhitespaceAndPreservesNotes()
    {
        var v = SqlServerWorkspace.ValidateObject(
            new ObjectProperties("  Объект  ", " Заказчик ", "  Адрес  ", " Папка ", "Примечание", "dns"));
        Assert.Equal("Объект", v.Name);
        Assert.Equal("Заказчик", v.Customer);
        Assert.Equal("Адрес", v.Address);
        Assert.Equal("Папка", v.Folder);
        Assert.Equal("Примечание", v.Note);
    }

    [Fact]
    public void ObjectValidation_RejectsEmptyOrOverlongNames()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspace.ValidateObject(new ObjectProperties(" ")));
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspace.ValidateObject(new ObjectProperties(new string('x', 256))));
    }

    [Fact]
    public void ValidationPreservesIndependentLegalFieldsWithoutSql()
    {
        var input = new SqlServerWorkspace.NewContractData(" main ", " N ", "01.01.2026", " A ", 5, 6)
        { AgreementId = 2147483648L, AnnexNumber = " 2 " };
        var result = WorkspaceInputPolicy.ValidateContract(input);
        Assert.Equal(SqlServerWorkspace.ValidateContract(input),result);
        Assert.Equal("main",result.Kind);
        Assert.Null(result.BaseContractId);
        Assert.Equal(2147483648L,result.AgreementId);
        Assert.Equal("2",result.AnnexNumber); // 1ced9101: политика нормализует номер приложения (Trim)
    }

    [Fact]
    public void ContractValidation_RejectsInvalidKind()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspace.ValidateContract(new("not-valid", "", "", "", null, null)));
    }

    [Theory]
    [InlineData("01.01.2026", true)]
    [InlineData("", true)]
    [InlineData("99.99.9999", false)]
    public void ContractValidation_ValidatesDate(string date, bool valid)
    {
        var data = new SqlServerWorkspace.NewContractData("kp", "", date, "", null, null);
        if (valid) Assert.Equal(date, SqlServerWorkspace.ValidateContract(data).Date);
        else Assert.Throws<ArgumentException>(() => SqlServerWorkspace.ValidateContract(data));
    }

    [Fact]
    public void Supplement_RequiresBaseDocument()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspace.ValidateContract(new("dop", "1", "", "", null, null)));
    }

    [Fact]
    public void EmptyKp_DropsUnrelatedBaseId()
    {
        var result = SqlServerWorkspace.ValidateContract(
            new("kp", "1", "20.11.2026", "Новая смета", null, 42));
        Assert.Null(result.BaseContractId);
    }

    [Fact]
    public void CopyContract_RejectsNonPilotCatalogWithoutConnecting()
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerWorkspace.CopyContract(
                "Server=localhost;Database=Production;Integrated Security=true", 1,
                new SqlServerWorkspace.NewContractData("kp", "", "20.11.2026", "Копия", null, null), "rev"));
    }

    [Fact]
    public void CopyContract_ValidatesDialogDataBeforeAnyConnection()
    {
        var pilot = "Server=localhost;Database=GmcSmetaPilot_UnitTest;Integrated Security=true";
        Assert.Throws<ArgumentException>(
            () => SqlServerWorkspace.CopyContract(pilot, 1,
                new SqlServerWorkspace.NewContractData("stage", "", "20.11.2026", "Копия", null, null), "rev"));
        Assert.Throws<ArgumentException>(
            () => SqlServerWorkspace.CopyContract(pilot, 1,
                new SqlServerWorkspace.NewContractData("dop", "", "20.11.2026", "Копия", null, null), "rev"));
        Assert.Throws<ArgumentNullException>(
            () => SqlServerWorkspace.CopyContract(pilot, 1, null!, "rev"));
    }

    [Fact]
    public void UpdateContract_RejectsNonPilotCatalogWithoutConnecting()
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlServerWorkspace.UpdateContract(
                "Server=localhost;Database=Production;Integrated Security=true", 1,
                new ContractProperties("kp", "", "20.11.2026", "Смета"), "rev"));
    }

    [Fact]
    public void UpdateContract_NullPropertiesRejectedBeforeAnyConnection()
    {
        Assert.Throws<ArgumentNullException>(
            () => SqlServerWorkspace.UpdateContract(
                "Server=localhost;Database=GmcSmetaPilot_UnitTest;Integrated Security=true", 1, null!, "rev"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Archive_RejectsNonPilotCatalogWithoutConnecting(bool archived)
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.SetObjectArchived(
            "Server=localhost;Database=Production;Integrated Security=true", 1, archived, "rev"));
    }

    [Fact]
    public void ArchiveYear_RejectsNonPilotCatalogWithoutConnecting()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.ChangeArchiveYear(
            "Server=localhost;Database=Production;Integrated Security=true", 1, 2031, "rev"));
    }

    [Fact]
    public void DeleteObject_RejectsNonPilotCatalogWithoutConnecting()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.DeleteObject(
            "Server=localhost;Database=Production;Integrated Security=true", 1, "rev"));
    }

    [Fact]
    public void DeleteContract_RejectsNonPilotCatalogWithoutConnecting()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.DeleteContract(
            "Server=localhost;Database=Production;Integrated Security=true", 1, "rev"));
    }

    [Fact]
    public void UpdateContract_RejectsSelfLinkAndInvalidDateBeforeConnecting()
    {
        const string pilot = "Server=localhost;Database=GmcSmetaPilot_UnitTest;Integrated Security=true";
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.UpdateContract(pilot, 1,
            new ContractProperties(Kind: "dop", BaseContractId: 1), "rev"));
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.UpdateContract(pilot, 1,
            new ContractProperties(Date: "31.02.2026"), "rev"));
        Assert.Throws<ArgumentException>(() => SqlServerWorkspace.UpdateContract(pilot, 1,
            new ContractProperties(VatRate: -1), "rev"));
    }

    [Fact]
    public void DeletePreviewsRejectProductionWithoutConnecting()
    {
        const string connection = "Server=localhost;Database=Production;Integrated Security=true";
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerWorkspace.PrepareObjectDelete(connection, 1, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerWorkspace.PrepareContractDelete(connection, 1, 2, "rev"));
    }
}
