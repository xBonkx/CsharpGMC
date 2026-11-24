using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Parse editable KS-2 cell text before any SQL mutation (ks_view.save_qty:
/// plain ru-number or simple arithmetic kept as «=…» for display, «КN» references
/// must start with «=» and are bound to stable @ids).</summary>
public static class ActQuantityInput
{
    public readonly record struct Parsed(decimal Quantity, string Formula);

    private static readonly Regex Reference = new(@"(?:[КKкk]\d+|@\d+)", RegexOptions.Compiled);
    private static readonly Regex Plain = new(@"^[+-]?(\d+\.?\d*|\.\d+)$", RegexOptions.Compiled);
    private static readonly Regex ExprOk = new(@"^[0-9+\-*/().]+$", RegexOptions.Compiled);

    private const decimal MaxNumber = 1_000_000_000_000m;

    public static Parsed Parse(string? text, IReadOnlyDictionary<long, int> visibleNumbers) =>
        Parse(text,visibleNumbers.ToDictionary(p=>p.Key,p=>p.Value.ToString(CultureInfo.InvariantCulture)));

    public static Parsed Parse(string? text, IReadOnlyDictionary<long, string> visibleNumbers)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new Parsed(0m, "");

        var linked = Reference.IsMatch(value);
        // «@42*2» — уже связанная формула (результат прошлого парса): без '=' её можно
        // перечитать из ячейки как есть. '=' требуется только для видимых ссылок «К2».
        if (linked && !value.StartsWith('=') && !value.StartsWith("@"))
            throw new InvalidOperationException("Начните формулу со знака =, например =К12+К15.");
        if (linked || value.StartsWith('='))
        {
            var raw = value.StartsWith('=') ? value[1..].Trim() : value;
            var bound = ActQuantityFormulas.Bind(raw, visibleNumbers);
            if (!bound.Contains('@'))
                throw new InvalidOperationException("Формула должна ссылаться на позицию (например, К2*1,5).");
            return new Parsed(0m, bound);
        }

        // обычное число или простая арифметика: «12*1,15» → 13,8 (формула «=12*1,15» — для показа)
        var body = value.Replace("\u00a0", "").Replace(" ", "").Replace(",", ".").Replace("₽", "")
            .Replace('−', '-').Replace('–', '-').TrimStart('=');
        if (body.Length == 0) return new Parsed(0m, "");
        decimal qty;
        if (Plain.IsMatch(body))
        {
            if (!decimal.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out qty) ||
                Math.Abs(qty) > MaxNumber)
                throw new InvalidOperationException($"Не число: «{value}». Пишите цифрами, например 12 или 1 250,50");
        }
        else
        {
            if (body.Length > 40 || body.Contains("**") || !ExprOk.IsMatch(body))
                throw new InvalidOperationException($"Не число: «{value}». Пишите цифрами, например 12 или 1 250,50");
            if (!TryEvaluate(body, out qty) || Math.Abs(qty) > MaxNumber)
                throw new InvalidOperationException($"Не число: «{value}». Пишите цифрами, например 12 или 1 250,50");
        }
        if (qty < 0m)
            throw new InvalidOperationException($"Отрицательное число здесь нельзя: «{value}»");
        var formula = Plain.IsMatch(body) ? "" : "=" + body;
        return new Parsed(qty == 0m ? 0m : qty, formula);
    }

    private static bool TryEvaluate(string expression, out decimal result)
    {
        result = 0m;
        var pos = 0;
        decimal ParseExpr()
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
        decimal ParseTerm()
        {
            var left = ParseFactor();
            while (pos < expression.Length)
            {
                var op = expression[pos];
                if (op != '*' && op != '/') break;
                pos++;
                var right = ParseFactor();
                if (op == '*') left *= right;
                else left /= right;      // деление на ноль → DivideByZeroException → «не число»
            }
            return left;
        }
        decimal ParseFactor()
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
                    throw new FormatException();
                pos++;
                return value;
            }
            var start = pos;
            while (pos < expression.Length && (char.IsDigit(expression[pos]) || expression[pos] == '.'))
                pos++;
            if (start == pos) throw new FormatException();
            return decimal.Parse(expression[start..pos], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        try
        {
            result = ParseExpr();
            if (pos != expression.Length) return false;
        }
        catch (DivideByZeroException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        return true;
    }
}
