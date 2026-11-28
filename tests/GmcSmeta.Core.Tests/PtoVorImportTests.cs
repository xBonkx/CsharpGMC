using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PtoVorImportTests
{
    private static PtoAttachment Attachment() => new()
    {Title="ВОР",Kind="ВОР",Sha256=new string('a',64),RelativePath="files/vor.xlsx"};
    private static PtoPackage Package()
    {
        var p=PtoRules.New(1,"Стройка","Адрес",42);
        p.Revision=7;p.Status="Готов";p.ManualReview=true;
        p.Positions.Add(new PtoWork{Name="Существующая работа"});
        return p;
    }
    [Fact]
    public void CandidateContainsAttachmentAndRowsButOriginalIsUnchangedUntilPublish()
    {
        var p=Package();var a=Attachment();
        var row=new PtoWork{Name="Монтаж",Qty="5",SourceActId=9,SourcePositionId=18};
        var originalList=p.Positions;
        var candidate=PtoVorImport.Prepare(p,a,new[]{row});
        Assert.Empty(p.Attachments);Assert.Single(p.Positions);Assert.Null(row.AttachmentId);
        Assert.True(p.ManualReview);Assert.Equal("Готов",p.Status);Assert.Equal(7L,p.Revision);
        Assert.Equal(7L,candidate.Revision);
        Assert.Equal(a.Id,candidate.Positions[^1].AttachmentId);
        Assert.Equal(18L,candidate.Positions[^1].SourcePositionId);
        Assert.Single(candidate.Attachments);Assert.Equal("Черновик",candidate.Status);
        Assert.False(candidate.ManualReview);
        // Simulate confirmed save; the original is untouched on ordinary failure or unknown commit.
        candidate.Revision=8;
        PtoVorImport.Publish(p,candidate);
        Assert.Same(originalList,p.Positions);Assert.Equal(2,p.Positions.Count);
        Assert.Equal(a.Id,p.Positions[^1].AttachmentId);Assert.Equal(8L,p.Revision);
        Assert.False(p.ManualReview);
    }
    [Fact]
    public void RejectsDuplicateActOrFileAndUnconfirmedPublication()
    {
        var p=Package();var a=Attachment();var row=new PtoWork{SourceActId=9};
        var candidate=PtoVorImport.Prepare(p,a,new[]{row});
        Assert.Throws<InvalidOperationException>(()=>PtoVorImport.Publish(p,candidate));
        candidate.Revision++;
        PtoVorImport.Publish(p,candidate);
        Assert.Throws<InvalidOperationException>(()=>PtoVorImport.Prepare(p,Attachment(),new[]{new PtoWork()}));
        var other=Attachment();other.Sha256=new string('b',64);
        Assert.Throws<InvalidOperationException>(()=>PtoVorImport.Prepare(p,other,new[]{row}));
        Assert.Single(p.Attachments);Assert.Equal(2,p.Positions.Count);
    }
    [Fact]
    public void RejectsEmptyOrAlreadyBoundRowsWithoutChangingPackage()
    {
        var p=Package();var a=Attachment();
        Assert.Throws<ArgumentException>(()=>PtoVorImport.Prepare(p,a,Array.Empty<PtoWork>()));
        Assert.Throws<ArgumentException>(()=>PtoVorImport.Prepare(p,a,new[]{new PtoWork{AttachmentId=a.Id}}));
        Assert.Empty(p.Attachments);Assert.Single(p.Positions);Assert.True(p.ManualReview);
    }
}
