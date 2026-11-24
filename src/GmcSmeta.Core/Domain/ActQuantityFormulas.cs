using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Ссылки на объёмы других позиций сметы в ячейках КС-2 (act_qty.py): пользователь
/// пишет «К2*1,5», перед сохранением видимые номера заменяются стабильными
/// id (@123), при показе — обратно. Calculate вычисляет значения формул с
/// зависимыми пересчётами по топологическому порядку (циклы/некорректные
/// выражения/отрицательные объёмы отвергаются).
/// </summary>
public static class ActQuantityFormulas
{
    private static readonly Regex Visible = new("[КK](\\d+(?:[.,]\\d+)*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Stored = new("@(\\d+)", RegexOptions.Compiled);

    /// <summary>Заменить видимые номера К2 на стабильные id перед сохранением (act_qty.bind).</summary>
    public static string Bind(string text, IReadOnlyDictionary<long, int> numbers)
    {
        var byNumber = numbers.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList());
        return Visible.Replace(text, match =>
        {
            var numberText = match.Groups[1].Value.Replace(',', '.');
            if (!int.TryParse(numberText, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                throw new InvalidOperationException($"Позиция не найдена: {match.Value}");
            if (!byNumber.TryGetValue(number, out var ids))
                throw new InvalidOperationException($"Позиция не найдена: {match.Value}");
            if (ids.Count != 1)
                throw new InvalidOperationException($"Номер неоднозначен: {match.Value}. Скопируйте ссылку через ПКМ по позиции.");
            return "@" + ids[0];
        });
    }

    /// <summary>Вернуть видимые номера для показа (act_qty.display).</summary>
    public static string Display(string formula, IReadOnlyDictionary<long, int> numbers) =>
        Stored.Replace(formula, m =>
        {
            var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return numbers.TryGetValue(id, out var number) ? "К" + number : m.Value;
        });

    /// <summary>
    /// Перепривязать стабильные id позиций (@123) после восстановления из корзины со
    /// сменой идентификаторов: известные id заменяются, неизвестные остаются (trash.restore).
    /// </summary>
    public static string RemapIds(string formula, IReadOnlyDictionary<long, long> ids) =>
        Stored.Replace(formula, m =>
        {
            var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return "@" + (ids.TryGetValue(id, out var mapped) ? mapped : id);
        });

    private static readonly Regex Junk = new("\\s+", RegexOptions.Compiled);

    /// <summary>
    /// Пересчитать значения формул без изменения строк (act_qty.calculate).
    /// lines: qty-формулы текущих строк; edits: (новое количество, новая формула);
    /// sources: id верхних строк сметы (на них могут ссылаться формулы).
    /// Возвращает пересчитанные количества для строк с формулами.
    /// </summary>
    public static IReadOnlyDictionary<long, decimal> Calculate(
        IReadOnlyDictionary<long, (string QtyFormula, decimal Qty)> lines,
        IReadOnlyDictionary<long, (decimal Qty, string Formula)> edits,
        IReadOnlySet<long> sources)
    {
        var formulas = new Dictionary<long, string>();
        foreach (var (pid, (qtyFormula, _)) in lines)
            if ((qtyFormula ?? "").Contains('@'))
                formulas[pid] = qtyFormula;
        foreach (var (pid, (_, formula)) in edits)
        {
            if ((formula ?? "").Contains('@')) formulas[pid] = formula;
            else formulas.Remove(pid);
        }
        if (formulas.Count == 0) return new Dictionary<long, decimal>();

        var graph = new Dictionary<long, List<long>>();
        foreach (var pid in formulas.Keys.ToList())
        {
            var text = Junk.Replace(formulas[pid], "").TrimStart('=')
                .Replace(',', '.').Replace('−', '-').Replace('–', '-');
            if (text.Length > 1024 || text.Contains("**") ||
                !Regex.IsMatch(Stored.Replace(text, "0"), "^[0-9+*/().-]+$"))
                throw new InvalidOperationException("Проверьте формулу объёма.");
            formulas[pid] = text;
            var refs = Stored.Matches(text).Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToHashSet();
            if (edits.ContainsKey(pid) && !refs.IsSubsetOf(sources.Select(s => s)))
                throw new InvalidOperationException("Ссылка должна вести на позицию сметы в этом акте.");
            graph[pid] = refs.Where(refs_ => formulas.ContainsKey(refs_)).ToList();
        }

        var values = new Dictionary<long, decimal>();
        foreach (var (pid, (_, qty)) in lines) values[pid] = qty;
        foreach (var (pid, (qty, _)) in edits) values[pid] = qty;

        // Топологический порядок с обнаружением циклов (graphlib.TopologicalSorter).
        var permanent = new HashSet<long>();
        var temporary = new HashSet<long>();
        var ordered = new List<long>();
        void Visit(long pid)
        {
            if (permanent.Contains(pid)) return;
            if (temporary.Contains(pid))
                throw new InvalidOperationException("Круговая ссылка: позиция прямо или через другие строки ссылается на себя.");
            temporary.Add(pid);
            foreach (var next in graph.GetValueOrDefault(pid) ?? new List<long>())
                if (formulas.ContainsKey(next)) Visit(next);
            temporary.Remove(pid);
            permanent.Add(pid);
            ordered.Add(pid);
        }
        foreach (var pid in formulas.Keys) Visit(pid);

        var result = new Dictionary<long, decimal>();
        foreach (var pid in ordered)
        {
            var expression = Stored.Replace(formulas[pid], m =>
                values.TryGetValue(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), out var v)
                    ? v.ToString("0.############", CultureInfo.InvariantCulture) : "0");
            var value = Evaluate(expression);
            if (!double.IsFinite(value) || value < 0 || value > 1e12)
                throw new InvalidOperationException("Формула должна давать неотрицательный объём до 1 трлн.");
            values[pid] = (decimal)Math.Round(value, 12);
            result[pid] = values[pid];
        }
        return result;
    }

    /// <summary>
    /// Безопасный вычислитель выражений из цифр и + * / ( ) − — только эти символы
    /// проходят валидацию, аналог eval без builtins в Python.
    /// </summary>
    private static double Evaluate(string expression)
    {
        var pos = 0;
        double ParseExpr()
        {
            var left = ParseTerm();
            while (pos < expression.Length)
            {
                var op = expression[pos];
                if (op != '+' && op != '-') break;
                pos++;
                var right = ParseTerm();
                left = op == '+' ? left + right : left - right;
            }
            return left;
        }
        double ParseTerm()
        {
            var left = ParseFactor();
            while (pos < expression.Length)
            {
                var op = expression[pos];
                if (op != '*' && op != '/') break;
                pos++;
                var right = ParseFactor();
                if (op == '*') left *= right;
                else left /= right;      // деление на ноль даёт NaN/∞ — отсечётся проверкой isfinite
            }
            return left;
        }
        double ParseFactor()
        {
            if (pos < expression.Length && expression[pos] == '-')
            {
                pos++;
                return -ParseFactor();
            }
            if (pos < expression.Length && expression[pos] == '+')
            {
                pos++;
                return ParseFactor();
            }
            if (pos < expression.Length && expression[pos] == '(')
            {
                pos++;
                var value = ParseExpr();
                if (pos >= expression.Length || expression[pos] != ')')
                    throw new FormatException("Скобки не закрыты.");
                pos++;
                return value;
            }
            var start = pos;
            while (pos < expression.Length && (char.IsDigit(expression[pos]) || expression[pos] == '.'))
                pos++;
            if (start == pos) throw new FormatException("Ожидалось число.");
            return double.Parse(expression[start..pos], CultureInfo.InvariantCulture);
        }
        var result = ParseExpr();
        if (pos != expression.Length) throw new FormatException("Лишние символы в формуле.");
        return result;
    }
}
