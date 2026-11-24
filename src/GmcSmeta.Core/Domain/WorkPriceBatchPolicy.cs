namespace GmcSmeta.Core.Domain;

/// <summary>Server-side validation for an atomic SQL Server work-price batch.</summary>
public static class WorkPriceBatchPolicy
{
    public const int MaxBatchRows = 10000;

    public static void ValidateSelection(long contractId,
        IReadOnlyList<long>? positionIds, decimal price)
    {
        if (contractId <= 0 || positionIds is null ||
            positionIds.Count is 0 or > MaxBatchRows ||
            positionIds.Any(id => id <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count)
            throw new ArgumentException("Некорректные позиции или документ для изменения цены работы.");
        if (price < 0m || price > NumberText.MaxNumber)
            throw new ArgumentOutOfRangeException(nameof(price),
                "Цена работы вне допустимого диапазона.");
    }

    public static void EnsureWritable(long contractId, long actualContractId,
        bool isRootPosition, bool locked, string? smrMode)
    {
        if (actualContractId != contractId)
            throw new InvalidOperationException(
                "Выделение содержит позицию другого договора. Изменение всей группы отменено.");
        if (!isRootPosition)
            throw new InvalidOperationException(
                "Комплектующая не может получать собственную цену работы.");
        if (locked)
            throw new InvalidOperationException("Подписанный документ нельзя редактировать.");
        if (smrMode != "pos")
            throw new InvalidOperationException(
                "Сначала включи режим СМР по позициям.");
    }
}
