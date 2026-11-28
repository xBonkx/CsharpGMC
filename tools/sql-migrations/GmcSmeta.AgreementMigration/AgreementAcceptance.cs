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
        Console.WriteLine("PASS agreement CRUD and transfer: before annex, own fields, two agreements, annex/DS inheritance, stale revision, cross-object protection, existing-context rejection and empty-target transfer.");
    }
}
