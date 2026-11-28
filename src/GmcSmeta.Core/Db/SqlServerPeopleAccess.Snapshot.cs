using System.Data;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed partial class SqlServerPeopleAccess
{
    internal static bool IsSameReadRevision(string before, string after) =>
        !string.IsNullOrWhiteSpace(before) && string.Equals(before, after, StringComparison.Ordinal);

    private static string ReadAccessRevision(SqlConnection conn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var revision = token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        token.ThrowIfCancellationRequested();
        return revision;
    }

    private static (IReadOnlyList<PeoplePerson> People, IReadOnlyList<PeopleCar> Cars) ReadDirectory(
        SqlConnection conn, bool showInactive, CancellationToken token)
    {
        var people = new List<PeoplePerson>();
        var cars = new List<PeopleCar>();
        using (var cmd = Command(conn, null,
            "SELECT id,fio,post,birth,note,fio_gen,post_gen,active,sort_order FROM dbo.people " +
            (showInactive ? "" : "WHERE active=1 ") + "ORDER BY sort_order,id"))
        using (var registration = SqlReadCancellation.Register(cmd, token))
        using (var reader = SqlReadCancellation.ExecuteReader(cmd, token))
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                people.Add(ReadPerson(reader));
            }
        using (var cmd = Command(conn, null,
            "SELECT id,model,plate,note,active,sort_order FROM dbo.cars " +
            (showInactive ? "" : "WHERE active=1 ") + "ORDER BY sort_order,id"))
        using (var registration = SqlReadCancellation.Register(cmd, token))
        using (var reader = SqlReadCancellation.ExecuteReader(cmd, token))
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                cars.Add(ReadCar(reader));
            }
        token.ThrowIfCancellationRequested();
        var sortedPeople = PeopleRules.SortPeople(people);
        var sortedCars = cars.OrderBy(x => x.SortOrder == 0 ? int.MaxValue : x.SortOrder)
            .ThenBy(x => x.Model).ThenBy(x => x.Plate).ToArray();
        token.ThrowIfCancellationRequested();
        return (sortedPeople, sortedCars);
    }

    // One guarded connection and one outer revision fence for every part of the
    // initial admission view. Existing directory/site/company/history readers
    // are reused without changing their scope, columns or order.
    public (AccessSite? Site, IReadOnlyList<AccessCompany> Companies,
        IReadOnlyList<AccessHistory> History,
        (IReadOnlyList<PeoplePerson> People, IReadOnlyList<PeopleCar> Cars, string Revision) Directory)
        ReadInitialSnapshot(long? objectId, bool showInactive, CancellationToken token = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var conn = Open(token);
            var before = ReadAccessRevision(conn, token);
            var site = objectId is long id ? ReadSite(conn, id, token) : null;
            var companies = site is null ? Array.Empty<AccessCompany>() : ReadCompanies(conn, token);
            var history = objectId is long historyId
                ? ReadHistory(conn, historyId, "list", token)
                : Array.Empty<AccessHistory>();
            var directory = ReadDirectory(conn, showInactive, token);
            var after = ReadAccessRevision(conn, token);
            if (IsSameReadRevision(before, after))
                return (site, companies, history, (directory.People, directory.Cars, after));
        }
        throw new DBConcurrencyException("Данные допуска изменились при чтении или отсутствует ревизия. Откройте список заново.");
    }
}
