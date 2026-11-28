using System.Windows;
using System.Windows.Input;

namespace GmcSmeta.App;

/// <summary>
/// Второе подтверждение удаления (перенос ConfirmDeleteDialog): красная кнопка включается
/// только ответом на маленький вопрос, каждый раз другой — чтобы не нажималось по привычке.
/// </summary>
public partial class ConfirmDeleteDialog : Window
{
    private readonly string _answer;

    public ConfirmDeleteDialog(string name, IReadOnlyList<string> details, string what = "объект")
    {
        InitializeComponent();
        Title = what switch { "карточка" => "Удаление карточки", "объект" => "Удаление объекта", _ => "Удаление документа" };
        HeadingText.Text = what == "карточка" ? "Удалить карточку и все её документы?" : $"Удалить {what}?";
        NameText.Text = "«" + name.Trim() + "»";
        DetailsText.Text = details.Count > 0
            ? "Вместе с ним удалится:\n• " + string.Join("\n• ", details)
            : what == "карточка" ? "Карточка пуста." : "Нет дополнительных связанных записей.";
        var (question, answer) = Challenge();
        QuestionText.Text = question;
        _answer = answer;
        Loaded += (_, _) => AnswerBox.Focus();
    }

    private static (string Question, string Answer) Challenge()
    {
        // dialogs._challenge: ответ только цифрами — без раскладок, тире и кавычек.
        switch (Random.Shared.Next(3))
        {
            case 0:
                var a = Random.Shared.Next(2, 10);
                var b = Random.Shared.Next(2, 10);
                return ($"Сколько будет {a} + {b}?", (a + b).ToString());
            case 1:
                var x = Random.Shared.Next(11, 99);
                var y = Random.Shared.Next(11, 99);
                while (y == x) y = Random.Shared.Next(11, 99);
                return ($"Какое число больше: {x} или {y}?", Math.Max(x, y).ToString());
            default:
                var n = Random.Shared.Next(10, 100);
                return ($"Впишите число {n}:", n.ToString());
        }
    }

    private bool AnswerConfirmed => !string.IsNullOrEmpty(_answer) && AnswerBox.Text.Trim() == _answer;

    private void AnswerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (DeleteButton is not null) DeleteButton.IsEnabled = AnswerConfirmed;
    }

    private void AnswerBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A correct answer enables Delete; submitting the text must not delete.
        if (e.Key == Key.Enter)
            e.Handled = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (AnswerConfirmed && DeleteButton.IsEnabled) DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
