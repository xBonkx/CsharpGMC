namespace GmcSmeta.Core.Domain;

/// <summary>Supplier lot settlement invariants shared by SQL and offline regression tests.</summary>
public static class MetalLotRules
{
    public static void ValidateOrder(string category, decimal area, decimal pieces)
    {
        if (category != "Бухты" || MetalWarehouseRules.Units(area) <= 0 ||
            MetalWarehouseRules.Pieces(pieces) <= 0)
            throw new ArgumentException("Партия должна содержать положительную площадь и число бухт.");
    }

    public static void ValidatePayment(bool paid)
    {
        if (paid) throw new InvalidOperationException("Партия уже оплачена.");
    }

    public static void ValidateReceipt(bool paid, string place,
        long orderedArea, int orderedPieces, long receivedArea, int receivedPieces,
        long requestArea, int requestPieces)
    {
        if (!paid)
            throw new InvalidOperationException("Сначала отметьте оплату партии.");
        if (!MetalWarehouseRules.Places.Contains(place) ||
            place == MetalWarehouseRules.SupplierPlace)
            throw new ArgumentException("Выберите физическое место получения.");
        var availableArea = orderedArea - receivedArea;
        var availablePieces = orderedPieces - receivedPieces;
        if (availableArea <= 0 || availablePieces <= 0)
            throw new ArgumentException("Партия уже полностью получена.");
        if (requestArea <= 0 || requestPieces <= 0 ||
            requestArea > availableArea || requestPieces > availablePieces ||
            (requestArea == availableArea) != (requestPieces == availablePieces))
            throw new ArgumentException("Площадь или число бухт не соответствуют остатку партии.");
    }
}
