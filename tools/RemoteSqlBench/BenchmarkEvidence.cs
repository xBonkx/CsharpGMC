using System.Globalization;

namespace GmcSmeta.RemoteSqlBench;

internal sealed record BenchmarkOptions(int Runs, int? ContractId, string OutputDirectory,
    string SourceSha, string Topology)
{
    internal static BenchmarkOptions Parse(string[] args)
    {
        int runs = 30;
        int? contract = null;
        var output = Path.Combine("tools", "RemoteSqlBench", "out");
        var sha = "UNSPECIFIED";
        var topology = "UNSPECIFIED";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]))
                throw new ArgumentException("Missing or repeated option.");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--runs" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                    n is >= 20 and <= 300: runs = n; break;
                case "--contract-id" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                    id > 0: contract = id; break;
                case "--out" when !string.IsNullOrWhiteSpace(value): output = value; break;
                case "--sha" when value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit): sha = value; break;
                case "--topology" when value is "LAN" or "WIFI" or "LOCAL" or "VPN" or "OTHER": topology = value; break;
                default: throw new ArgumentException("Invalid benchmark option.");
            }
        }
        return new BenchmarkOptions(runs, contract, output, sha, topology);
    }
}

internal sealed record BenchmarkSample(int Iteration, string Status, int? Rows,
    double ElapsedMs, long AllocatedBytes, string ErrorType);

internal sealed record BenchmarkSummary(int Count, int? Rows, bool RowsStable, double Min,
    double P50, double P95, double? P99, double Max, long MedianAllocatedBytes);

internal sealed record BenchmarkCase(string Name, int? SecondContractId, string Status, string Reason,
    IReadOnlyList<BenchmarkSample> Samples, BenchmarkSummary? Summary);

internal static class BenchmarkStatistics
{
    internal static BenchmarkSummary Summarize(IEnumerable<BenchmarkSample> samples)
    {
        var completed = samples.Where(s => s.Iteration > 0 && s.Status == "OK").ToArray();
        if (completed.Length == 0) throw new ArgumentException("No completed measurements.");
        var times = completed.Select(s => s.ElapsedMs).Order().ToArray();
        var bytes = completed.Select(s => s.AllocatedBytes).Order().ToArray();
        var rows = completed[0].Rows;
        var stable = completed.All(s => s.Rows == rows);
        double Nearest(double fraction) => times[(int)Math.Ceiling(times.Length * fraction) - 1];
        var median = (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2d;
        return new BenchmarkSummary(times.Length, stable ? rows : null, stable,
            times[0], median, Nearest(.95), times.Length >= 100 ? Nearest(.99) : null,
            times[^1], bytes[(bytes.Length - 1) / 2]);
    }
}
