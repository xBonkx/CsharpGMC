namespace GmcSmeta.Core.Domain;

/// <summary>Mode-specific wording only: no changes to navigation membership or writes.</summary>
public sealed record NavigationModeCaptions(string Create, string CreateHint, string Search,
    string EmptyHeading, string EmptyHint)
{
    public static NavigationModeCaptions For(NavigationMode mode) => mode switch
    {
        NavigationMode.Kp => new("Новое КП", "Создать карточку КП (Ctrl+N)", "Поиск КП",
            "Выберите КП слева", "Создайте новое КП или загрузите готовое из Excel."),
        NavigationMode.Archive => new("Новый договор", "Создать карточку договора (Ctrl+N)", "Поиск в архиве",
            "Выберите документ в архиве", "Для работы с активными документами перейдите в «Договоры» или «КП»."),
        _ => new("Новый договор", "Создать карточку договора (Ctrl+N)", "Поиск договора",
            "Выберите договор слева", "Создайте новый договор или загрузите смету из Excel.")
    };
}
