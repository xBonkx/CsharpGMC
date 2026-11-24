using System.IO;
using System.Globalization;
using ClosedXML.Excel;

namespace GmcSmeta.App;

/// <summary>
/// Safe, deliberately small Python-compatible Excel formula evaluator.
/// Supports numeric constants, cell references, SUM(A1:B2), parentheses, + - * /.
/// No arbitrary calls, macros, external links, circular references or oversized ranges.
/// </summary>
internal sealed class MetalFormulaEvaluator
{
    private readonly IXLWorksheet _sheet;
    private readonly Dictionary<string, decimal> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MetalFormulaEvaluator(IXLWorksheet sheet) => _sheet = sheet;
    public void Clear() => _cache.Clear();

    public decimal Calculate(string address) => EvaluateCell(address.ToUpperInvariant(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private decimal EvaluateCell(string address, HashSet<string> visiting)
    {
        if (_cache.TryGetValue(address, out var cached)) return cached;
        if (visiting.Contains(address) || visiting.Count >= 100)
            throw new InvalidDataException("Циклическая формула.");
        var cell = _sheet.Cell(address);
        if (!cell.HasFormula)
        {
            var value = cell.CachedValue;
            if (value.IsBlank) return 0;
            if (value.IsNumber) return Convert.ToDecimal(value.GetNumber(), CultureInfo.InvariantCulture);
            throw new InvalidDataException("Текст нельзя использовать в арифметике.");
        }
        if (cell.FormulaA1.Contains("#REF!",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("#REF!");
        visiting.Add(address);
        try
        {
            var parser = new Parser(cell.FormulaA1.Replace("$", "").Trim().TrimStart('='),
                name => EvaluateCell(name, visiting),
                (left,right) => SumRange(left,right,visiting));
            var result = parser.Parse();
            _cache[address] = result;
            return result;
        }
        finally { visiting.Remove(address); }
    }

    private decimal SumRange(string left, string right, HashSet<string> visiting)
    {
        var a = _sheet.Cell(left).Address;
        var b = _sheet.Cell(right).Address;
        var firstRow = Math.Min(a.RowNumber,b.RowNumber);
        var lastRow = Math.Max(a.RowNumber,b.RowNumber);
        var firstCol = Math.Min(a.ColumnNumber,b.ColumnNumber);
        var lastCol = Math.Max(a.ColumnNumber,b.ColumnNumber);
        if ((long)(lastRow-firstRow+1)*(lastCol-firstCol+1)>10000)
            throw new InvalidDataException("Диапазон формулы слишком большой.");
        decimal total = 0;
        for (var row = firstRow; row <= lastRow; row++)
            for (var col = firstCol; col <= lastCol; col++)
                total += EvaluateCell(_sheet.Cell(row,col).Address.ToString(), visiting);
        return total;
    }

    private sealed class Parser
    {
        private readonly string _expression;
        private readonly Func<string,decimal> _reference;
        private readonly Func<string,string,decimal> _sum;
        private int _index;

        public Parser(string expression, Func<string,decimal> reference,
            Func<string,string,decimal> sum)
        {
            _expression = expression.ToUpperInvariant();
            _reference = reference;
            _sum = sum;
        }

        public decimal Parse()
        {
            var result = Expression();
            Skip();
            if (_index != _expression.Length)
                throw new InvalidDataException("Неизвестная формула Excel.");
            return result;
        }

        private decimal Expression()
        {
            var value = Term();
            while (true)
            {
                Skip();
                if (Take('+')) value = checked(value + Term());
                else if (Take('-')) value = checked(value - Term());
                else break;
            }
            return value;
        }

        private decimal Term()
        {
            var value = Factor();
            while (true)
            {
                Skip();
                if (Take('*')) value = checked(value * Factor());
                else if (Take('/')) value = value / Factor();
                else break;
            }
            return value;
        }

        private decimal Factor()
        {
            Skip();
            if (Take('+')) return Factor();
            if (Take('-')) return -Factor();
            if (Take('('))
            {
                var value = Expression();
                Require(')');
                return value;
            }
            if (_index >= _expression.Length)
                throw new InvalidDataException("Неполное выражение.");
            if (char.IsAsciiDigit(_expression[_index]) || _expression[_index] == '.')
            {
                var first = _index;
                while (_index < _expression.Length &&
                       (char.IsAsciiDigit(_expression[_index]) || _expression[_index] == '.'))
                    _index++;
                // Python AST accepts e.g. 1e3, preserve it for the small formula subset.
                if (_index < _expression.Length && _expression[_index] == 'E')
                {
                    _index++;
                    if (_index < _expression.Length && _expression[_index] is '+' or '-') _index++;
                    while (_index < _expression.Length && char.IsAsciiDigit(_expression[_index])) _index++;
                }
                if (!decimal.TryParse(_expression[first.._index],NumberStyles.Float,
                        CultureInfo.InvariantCulture,out var number))
                    throw new InvalidDataException("Некорректное число в формуле.");
                return number;
            }
            if (char.IsLetter(_expression[_index]))
            {
                var word = ReadName();
                if (word == "SUM" && Take('('))
                {
                    var left = ReadName();
                    Require(':');
                    var right = ReadName();
                    Require(')');
                    CheckReference(left);
                    CheckReference(right);
                    return _sum(left,right);
                }
                CheckReference(word);
                return _reference(word);
            }
            throw new InvalidDataException("Неподдерживаемая формула.");
        }

        private static void CheckReference(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name,@"^[A-Z]{1,3}[1-9][0-9]*$");
            if (!match.Success) throw new InvalidDataException("Формула содержит неподдерживаемую ссылку.");
        }

        private string ReadName()
        {
            Skip();
            var start = _index;
            while (_index < _expression.Length && char.IsAsciiLetterOrDigit(_expression[_index]))
                _index++;
            if (start == _index) throw new InvalidDataException("Отсутствует адрес ячейки.");
            return _expression[start.._index];
        }

        private void Require(char ch)
        {
            Skip();
            if (!Take(ch)) throw new InvalidDataException("Неполная формула.");
        }

        private bool Take(char ch)
        {
            Skip();
            if (_index < _expression.Length && _expression[_index] == ch)
            {
                _index++;
                return true;
            }
            return false;
        }
        private void Skip()
        {
            while (_index < _expression.Length && char.IsWhiteSpace(_expression[_index])) _index++;
        }
    }
}
