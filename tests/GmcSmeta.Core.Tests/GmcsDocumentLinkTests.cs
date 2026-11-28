using System.Text.Json;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class GmcsDocumentLinkTests
{
    private static ObjectRow Obj(int id, string name) => new(id, name, "", "", false, "");
    private static DocRow Doc(int id, int owner, string title, string number = "1") =>
        new(id, owner, "main", "Договор", title, "", number, "01.01.2026", false, 0, 0, 0, 0);
    private static readonly ObjectRow[] Objects = [Obj(1, "A"), Obj(2, "B")];
    private static readonly DocRow[] Documents = [Doc(10, 1, "First"), Doc(20, 2, "Second"), Doc(21, 2, "Third")];

    [Fact]
    public void SerializationRoundTripsV1WithoutConnectionString()
    {
        var json = GmcsDocumentLink.Serialize(Documents[0], "A", "tester", new DateTime(2026, 11, 28, 10, 15, 0));
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("", parsed.RootElement.GetProperty("db").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("tester", parsed.RootElement.GetProperty("saved_by").GetString());
        Assert.Equal(10, GmcsDocumentLink.Parse(json)!.Resolve(Objects, Documents));
    }

    [Theory]
    [InlineData(10,1,"B","Second",10)]
    [InlineData(10,0,"B","Second",10)]
    [InlineData(10,99,"A","Second",10)]
    [InlineData(10,2,"B","Second",20)]
    [InlineData(999,2,"B","Third",21)]
    [InlineData(999,2,"B","Missing",20)]
    public void ExistingIdThenTitleThenLegacyTuplePrecedence(int id,int owner,string name,string title,int expected)
    {
        var link = new GmcsDocumentLink(id,owner,name,title,"main","1","01.01.2026");
        Assert.Equal(expected,link.Resolve(Objects,Documents));
    }

    [Fact]
    public void UnknownTypeAndUnmatchedLinkDoNotOpen()
    {
        Assert.Null(GmcsDocumentLink.Parse("""{"type":"other"}"""));
        Assert.Null(GmcsDocumentLink.Parse("""{"object":"Missing"}""")!.Resolve(Objects,Documents));
    }

    [Fact]
    public void OldLinksWithoutTypeRemainReadable()
    {
        Assert.Equal(10,GmcsDocumentLink.Parse("""{"contract_id":10}""")!.Resolve(Objects,Documents));
    }
}
