using System.Globalization;

namespace GmcSmeta.Core;

/// <summary>
/// Деньги проекта. Внутри — <see cref="decimal"/> (точные десятичные дроби),
/// округление — до копеек «половина вверх», как это делает Excel и бухгалтерия
/// (в Python-версии для этого был костыль xround вокруг float).
/// </summary>
public static class Money
{
    /// <summary>Полкопейки и выше — вверх. Так считает Excel (4882,895 → 4882,90).</summary>
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сумма строки: количество × цена, округлённая до копеек.</summary>
    public static decimal Amount(decimal qty, decimal price) => Round2(qty * price);

    /// <summary>НДС, уже включённый в сумму: total × ставка / (100 + ставка).</summary>
    public static decimal VatIncluded(decimal total, decimal rate) =>
        rate == 0m ? 0m : total * rate / (100m + rate);

    /// <summary>НДС сверху (форма заказчика «ДНС»): net × ставка / 100, до копеек.</summary>
    public static decimal VatOnTop(decimal net, decimal rate) => Round2(net * rate / 100m);

    /// <summary>Сумма без НДС, если НДС в цене уже есть.</summary>
    public static decimal NetFromGross(decimal total, decimal rate) =>
        rate == 0m ? total : total * 100m / (100m + rate);

    /// <summary>Число из базы (REAL) в decimal: без «хвостов» двоичной дроби, максимум 6 знаков.</summary>
    public static decimal FromDouble(double value) =>
        double.IsFinite(value) ? decimal.Round((decimal)value, 6, MidpointRounding.AwayFromZero) : 0m;

    /// <summary>Деньги для текста и подсказок: 1 234 567,89 (русский формат).</summary>
    public static string Format(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
}
