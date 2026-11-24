using System.Data;
using System.Data.Common;
using System.Globalization;

namespace GmcSmeta.Core.Db;

/// <summary>Bound, non-empty SQL Server contract scope; no user-provided SQL fragments.</summary>
internal sealed class ContractReadScope
{
    internal const int BatchSize = 512;
    private readonly int[] _ids;
    private readonly string _parameters;

    internal ContractReadScope(IEnumerable<int> ids)
    {
        _ids = ids.Distinct().OrderBy(id => id).ToArray();
        if (_ids.Length == 0 || _ids.Length > BatchSize || _ids.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(ids));
        _parameters = string.Join(",", Enumerable.Range(0, _ids.Length).Select(Name));
    }

    private static string Name(int index) => "@scope" + index.ToString(CultureInfo.InvariantCulture);

    // Column is supplied exclusively by fixed, internal query templates.
    internal string Predicate(string column) => column + " IN (" + _parameters + ")";

    internal void Bind(DbCommand command)
    {
        for (var i = 0; i < _ids.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = Name(i);
            parameter.DbType = DbType.Int64;
            parameter.Value = (long)_ids[i];
            command.Parameters.Add(parameter);
        }
    }
}
