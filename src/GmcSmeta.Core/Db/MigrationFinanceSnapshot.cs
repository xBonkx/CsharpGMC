using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Independent financial regression gate for the isolated SQL rehearsal.
/// It exercises the application calculation readers, not a second approximation
/// of their formulas. Equality is exact (including sub-cent internal values).
/// This is not a Python-runtime parity certificate or permission to write v31.</summary>
public sealed record MigrationFinanceSnapshot(
    IReadOnlyDictionary<int,ContractTotals> Contracts,
    IReadOnlyDictionary<long,SqlServerActs.ActInfo> Acts,
    IReadOnlyDictionary<(long DocumentId,string Key),MigrationFinanceSnapshot.GroupAmounts> Groups)
{
    public sealed record GroupAmounts(decimal Net,decimal Vat,decimal Gross,
        decimal Year,decimal SinceStart,bool GrossCumulative,int ActCount);

    public static MigrationFinanceSnapshot Read(SqlConnection connection,int expectedVersion)
    {
        SqlMigrationPlan.RequireRehearsalSource(connection.Database);
        return ReadCore(connection,expectedVersion);
    }

    public static MigrationFinanceSnapshot ReadApprovedAgreementMigration(SqlConnection connection,int expectedVersion)
    {
        if (!SqlSchemaCompatibility.IsApprovedCutover(connection.Database,connection.DataSource) &&
            !SqlSchemaCompatibility.IsAgreementTestDatabase(connection.Database,connection.DataSource))
            throw new InvalidOperationException("This migration snapshot requires the approved office database or its dedicated local Agreement Pilot.");
        return ReadCore(connection,expectedVersion);
    }

    private static MigrationFinanceSnapshot ReadCore(SqlConnection connection,int expectedVersion)
    {
        _=SqlMigrationPlan.Script(expectedVersion,expectedVersion);
        using(var cmd=new SqlCommand(
            "SET QUOTED_IDENTIFIER ON; SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'",connection))
        using(var reader=cmd.ExecuteReader())
        {
            if(!reader.Read()||
                Convert.ToString(reader.GetValue(0),CultureInfo.InvariantCulture)!=expectedVersion.ToString(CultureInfo.InvariantCulture)
                ||reader.Read())throw new InvalidOperationException("Unexpected finance snapshot schema.");
        }
        var contracts=Totals.ReadSqlBatch(connection,null,out _,expectedVersion).ToDictionary(c=>c.Id);
        var acts=new Dictionary<long,SqlServerActs.ActInfo>();
        var groups=new Dictionary<(long DocumentId,string Key),GroupAmounts>();
        foreach(var contract in contracts.Values.OrderBy(c=>c.Id))
        {
            var local=SqlServerActs.ActsOfVersion(connection,contract.Id,null,expectedVersion);
            foreach(var act in local)acts.Add(act.Id,act);
            // Also exercise the real legal-agreement scope and its cumulative KS3
            // opening-balance logic. Unrelated KP documents have no legal scope.
            if(local.Count==0||contract.Kind is not ("main" or "dop"))continue;
            var scope=SqlServerKsContractScope.Read(connection,contract.Id,expectedVersion);
            foreach(var group in KsWorkspaceProjection.Build(scope.Acts))
            {
                var report=KsGroupReport.Calculate(scope,group.Key);
                groups.Add((contract.Id,group.Key),new(report.PeriodNet,report.PeriodVat,
                    report.PeriodGross,report.YearNet,report.SinceStartNet,report.GrossCumulative,report.Acts.Count));
            }
        }
        using(var count=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.acts",connection))
            if(SqlInteger.RequireInt64(count.ExecuteScalar())!=acts.Count)
                throw new InvalidOperationException("Incomplete act finance snapshot.");
        return new(contracts,acts,groups);
    }

    public void RequireSame(MigrationFinanceSnapshot actual,string stage)
    {
        Equal(Contracts,actual.Contracts,"contract",stage);
        Equal(Acts,actual.Acts,"act",stage);
        Equal(Groups,actual.Groups,"KS3 group",stage);
    }

    private static void Equal<TKey,TValue>(IReadOnlyDictionary<TKey,TValue> expected,
        IReadOnlyDictionary<TKey,TValue> actual,string kind,string stage) where TKey:notnull
    {
        if(expected.Count!=actual.Count)
            throw new InvalidOperationException($"{stage}: {kind} count changed.");
        foreach(var (key,value) in expected)
            if(!actual.TryGetValue(key,out var next)||!EqualityComparer<TValue>.Default.Equals(value,next))
            {
                // Group numbers may contain customer text: log only the kind/stage,
                // not monetary values, names or document numbers.
                throw new InvalidOperationException($"{stage}: {kind} calculation or identity changed.");
            }
    }
}
