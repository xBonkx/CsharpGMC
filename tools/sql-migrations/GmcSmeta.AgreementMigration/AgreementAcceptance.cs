using System.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class AgreementAcceptance
{
    internal static void Run(string connection)
    {
        using var read=SqlServerDatabase.Open(connection);
        string Revision()=>SqlServerDatabase.Revision(read);
        var obj=SqlServerWorkspace.CreateObject(connection,new ObjectProperties("Synthetic agreement acceptance"),Revision());
        var snapshot=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj));
        if(!snapshot.LegalEnabled||snapshot.LegalCards.Count!=0)throw new Exception("Independent empty agreement surface not available.");
        var print=snapshot.Print with { CustomerFull="Synthetic customer",CustomerOrganization="Synthetic customer",WorkTitle="Synthetic works",WorksStart="01.01.2026" };
        var legal=new SqlServerAgreements.Values(0,obj,"TEST-1","01.01.2026",null,"Synthetic legal subject","31.12.2026","Address","Synthetic contractor","Director","Signer",print);
        SqlServerWorkspace.SaveAgreementCard(connection,checked((int)obj),snapshot.Properties,print,Revision(),legal);
        snapshot=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj));
        if(snapshot.SelectedLegal?.Number!="TEST-1"||snapshot.Documents.Count!=0)throw new Exception("An agreement cannot exist independently of annexes.");
        var id=snapshot.SelectedLegal.Id;
        var document=SqlServerWorkspace.CreateEmptyContract(connection,obj,new("main","TEST-1","01.01.2026","Annex",null,null){AgreementId=id},Revision());
        if(SqlServerAgreements.DocumentAgreementId(read,document)!=id)throw new Exception("Annex legal inheritance failed.");
        var ds=SqlServerWorkspace.CreateEmptyContract(connection,obj,new("dop","4","11.08.2026","Supplement",null,document),Revision());
        if(SqlServerAgreements.DocumentAgreementId(read,ds)!=id)throw new Exception("DS legal inheritance failed.");
        var oldRevision=Revision();
        snapshot=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj));
        SqlServerWorkspace.SaveAgreementCard(connection,checked((int)obj),snapshot.Properties,print,oldRevision,snapshot.SelectedLegal! with{Subject="Changed"});
        try
        {
            SqlServerWorkspace.SaveAgreementCard(connection,checked((int)obj),snapshot.Properties,print,oldRevision,snapshot.SelectedLegal!);
            throw new Exception("Stale agreement update was accepted.");
        }
        catch(System.Data.DBConcurrencyException) { }
        snapshot=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj),0);
        SqlServerWorkspace.SaveAgreementCard(connection,checked((int)obj),snapshot.Properties,print,Revision(),legal with{Number="TEST-2"});
        if(SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj)).LegalCards.Count!=2)throw new Exception("Multiple agreements on one construction object failed.");
        var other=SqlServerWorkspace.CreateObject(connection,new ObjectProperties("Other synthetic object"),Revision());
        try
        {
            SqlServerWorkspace.CreateEmptyContract(connection,other,new("main","X","","Wrong object",null,null){AgreementId=id},Revision());
            throw new Exception("Cross-object agreement was accepted.");
        }
        catch(ArgumentException) { }
        var transferSource=SqlServerWorkspace.CreateObject(connection,new ObjectProperties("Synthetic transfer source"),Revision());
        var transferCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)transferSource));
        var transferPrint=print with{ObjectId=transferSource};
        SqlServerWorkspace.SaveAgreementCard(connection,checked((int)transferSource),transferCard.Properties,
            transferPrint,Revision(),legal with{ObjectId=transferSource,Number="TRANSFER-1",Print=transferPrint});
        var transferLegal=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)transferSource)).SelectedLegal!;
        var annex=SqlServerWorkspace.CreateEmptyContract(connection,transferSource,
            new("main","TRANSFER-1","01.01.2026","Transfer annex",null,null){AgreementId=transferLegal.Id},Revision());
        var supplement=SqlServerWorkspace.CreateEmptyContract(connection,transferSource,
            new("dop","5","11.08.2026","Transfer supplement",null,annex),Revision());
        var described=SqlServerWorkspace.DescribeContractTransfer(connection,annex);
        if(described.DocumentIds.Count!=2||!described.DocumentIds.Contains(supplement))
            throw new Exception("Transfer group lost supplement.");
        var transferRejected=false;
        try { SqlServerWorkspace.TransferContract(connection,annex,transferSource,obj,described.DocumentIds,described.Revision); }
        catch(InvalidOperationException) { transferRejected=true; }
        if(!transferRejected)throw new Exception("Transfer into existing legal context was accepted.");
        if(Revision()!=described.Revision||SqlServerAgreements.DocumentAgreementId(read,annex)!=transferLegal.Id||
            SqlServerAgreements.DocumentAgreementId(read,supplement)!=transferLegal.Id)
            throw new Exception("Rejected transfer changed revision or legal identity.");
        SqlServerWorkspace.TransferContract(connection,annex,transferSource,other,described.DocumentIds,described.Revision);
        var destination=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)other)).SelectedLegal;
        if(destination is null||destination.Id==transferLegal.Id||destination.Number!=transferLegal.Number||
            SqlServerAgreements.DocumentAgreementId(read,annex)!=destination.Id||
            SqlServerAgreements.DocumentAgreementId(read,supplement)!=destination.Id)
            throw new Exception("Transfer did not preserve legal requisites and group identity.");
        // Independent, nonce-owned empty object with two distinct legal cards.
        var trashObject=SqlServerWorkspace.CreateObject(connection,new ObjectProperties("Synthetic legal trash "+Guid.NewGuid().ToString("N")),Revision());
        var trashCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)trashObject));
        var trashPrint=print with{ObjectId=trashObject};
        foreach(var number in new[]{"TRASH-1","TRASH-2"})
        {
            SqlServerWorkspace.SaveAgreementCard(connection,checked((int)trashObject),trashCard.Properties,
                trashPrint,Revision(),legal with{ObjectId=trashObject,Number=number,Print=trashPrint});
        }
        var original=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)trashObject));
        var previousTrash=SqlServerTrash.List(connection).Select(t=>t.Id).ToHashSet();
        var deleteRevision=Revision();
        var deletePreview=SqlServerWorkspace.PrepareObjectDelete(connection,trashObject,deleteRevision);
        if(deletePreview.DocumentIds.Count!=0 || Revision()!=deleteRevision ||
            SqlServerTrash.List(connection).Any(t=>!previousTrash.Contains(t.Id)))
            throw new Exception("Delete preview changed data or unexpectedly found documents.");
        var wrongScopeRejected=false;
        try { SqlServerWorkspace.DeleteObject(connection,trashObject,deleteRevision,new long[]{long.MaxValue}); }
        catch(DBConcurrencyException) { wrongScopeRejected=true; }
        if(!wrongScopeRejected||Revision()!=deleteRevision||
            SqlServerTrash.List(connection).Any(t=>!previousTrash.Contains(t.Id)))
            throw new Exception("Unconfirmed delete scope modified data.");
        SqlServerWorkspace.DeleteObject(connection,trashObject,deleteRevision,deletePreview.DocumentIds);
        var trash=SqlServerTrash.List(connection).Single(t=>!previousTrash.Contains(t.Id)&&t.Title==original.Properties.Name);
        var trashRevision=Revision();
        var forgetRejected=false;
        try { SqlServerTrash.Forget(connection,trash.Id,trashRevision); }
        catch(InvalidOperationException) { forgetRejected=true; }
        if(!forgetRejected||Revision()!=trashRevision||!SqlServerTrash.List(connection).Any(t=>t.Id==trash.Id))
            throw new Exception("Legal trash snapshot was removed or revision changed.");
        var restored=SqlServerTrash.Restore(connection,trash.Id,trashRevision);
        var restoredCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)restored.Id));
        if(restored.Kind!="object"||restored.Id==trashObject||restoredCard.Documents.Count!=0||
            restoredCard.LegalCards.Count!=2||SqlServerTrash.List(connection).Any(t=>t.Id==trash.Id))
            throw new Exception("Legal trash restore lost root or cards.");
        foreach(var old in original.LegalCards)
        {
            var actual=restoredCard.LegalCards.Single(c=>c.Number==old.Number);
            if(actual.Id==old.Id||actual!=(old with{Id=actual.Id,ObjectId=restored.Id,Print=old.Print with{ObjectId=restored.Id}}))
                throw new Exception("Legal trash restore changed independent requisites.");
        }
        Console.WriteLine("PASS independent legal cards trash roundtrip and permanent-delete rejection.");
        var beforeDirectorRevision=Revision();
        var directorBefore=SqlServerDirector.DirectorRows(connection,"contracts").Single(r=>r.ObjectId==obj);
        if(directorBefore.LegalScope is not {AgreementCount:1,LinkedDocumentCount:2,UnlinkedDocumentCount:0})
            throw new Exception("Director counted an agreement without contributing documents.");
        if(Revision()!=beforeDirectorRevision)throw new Exception("Director read changed revision.");
        var secondLegal=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj)).LegalCards.Single(c=>c.Id!=id);
        var secondAnnex=SqlServerWorkspace.CreateEmptyContract(connection,obj,
            new("main",secondLegal.Number,secondLegal.Date,"Second legal annex",null,null){AgreementId=secondLegal.Id},Revision());
        var beforeCardRead=Revision();
        var firstCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj),id);
        var secondCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj),secondLegal.Id);
        var newCard=SqlServerWorkspace.ReadAgreementCard(connection,checked((int)obj),0);
        if(!firstCard.Documents.Select(d=>(long)d.Document.Id).ToHashSet().SetEquals(new[]{document,ds}) ||
            !secondCard.Documents.Select(d=>(long)d.Document.Id).ToHashSet().SetEquals(new[]{secondAnnex}) ||
            newCard.Documents.Count!=0 || Revision()!=beforeCardRead)
            throw new Exception("Legal card annex list leaked another agreement or modified data.");
        Console.WriteLine("PASS legal card annex scope and new empty card.");
        var afterDirectorRevision=Revision();
        var directorAfter=SqlServerDirector.DirectorRows(connection,"contracts").Single(r=>r.ObjectId==obj);
        if(directorAfter.LegalScope is not {AgreementCount:2,LinkedDocumentCount:3,UnlinkedDocumentCount:0}||
            directorAfter.Total!=directorBefore.Total||directorAfter.Closed!=directorBefore.Closed||
            directorAfter.Earned!=directorBefore.Earned||Revision()!=afterDirectorRevision)
            throw new Exception("Director legal annotation changed object totals or lost contributing identity.");
        Console.WriteLine("PASS director scope: independent agreements, empty-card exclusion, preserved object totals and revision.");
        var invalidAnnexRevision=Revision();
        var invalidAnnexRejected=false;
        try
        {
            SqlServerWorkspace.CreateEmptyContract(connection,obj,
                new("main","WRONG","01.01.2026","Wrong legal metadata",null,null){AgreementId=id},invalidAnnexRevision);
        }
        catch(InvalidOperationException) { invalidAnnexRejected=true; }
        if(!invalidAnnexRejected||Revision()!=invalidAnnexRevision)
            throw new Exception("Mismatched new annex metadata was accepted or changed revision.");
        var copied=SqlServerWorkspace.CopyContract(connection,document,
            new("main",secondLegal.Number,secondLegal.Date,"Copy positions across legal cards",secondLegal.CompanyId,null)
            {AgreementId=secondLegal.Id},Revision(),withPositions:false);
        if(SqlServerAgreements.DocumentAgreementId(read,copied)!=secondLegal.Id ||
            SqlServerAgreements.DocumentAgreementId(read,document)!=id)
            throw new Exception("Copy source incorrectly overrode explicit target legal identity.");
        Console.WriteLine("PASS new-annex legal metadata rollback and explicit cross-card copy.");
        Console.WriteLine("PASS agreement CRUD and transfer: before annex, own fields, two agreements, annex/DS inheritance, stale revision, cross-object protection, existing-context rejection and empty-target transfer.");
    }
}
