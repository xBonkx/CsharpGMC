using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class SqlConnectionDialog : Window
{
    private readonly List<RemoteSqlProfile> _profiles = [];
    private bool _loading;
    private bool _busy;
    private bool _closed;

    public string? SelectedConnectionString { get; private set; }
    public bool DemoRequested { get; private set; }

    public SqlConnectionDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _closed = true;
        try { _profiles.AddRange(RemoteSqlProfileStore.Load()); }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message + "\nНастройки не будут удалены автоматически.",
                "Профили SQL Server", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshProfiles(_profiles.FirstOrDefault());
        if (_profiles.Count == 0)
        {
            _loading = true;
            ProfileNameBox.Text = "Ноутбук — домашний сервер";
            _loading = false;
        }
        UpdateAuthentication();
    }

    private void RefreshProfiles(RemoteSqlProfile? selected)
    {
        _loading = true;
        ProfilePicker.ItemsSource = null;
        ProfilePicker.ItemsSource = _profiles.ToArray();
        ProfilePicker.SelectedItem = selected;
        _loading = false;
        if (selected is not null) ApplyProfile(selected);
    }

    private void ProfilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ProfilePicker.SelectedItem is RemoteSqlProfile profile)
            ApplyProfile(profile);
    }

    private void ApplyProfile(RemoteSqlProfile profile)
    {
        _loading = true;
        ProfileNameBox.Text = profile.Name;
        ServerBox.Text = profile.Server;
        PortBox.Text = profile.Port.ToString();
        DatabaseBox.Text = profile.Database;
        AuthBox.SelectedIndex = profile.WindowsAuthentication ? 0 : 1;
        UserBox.Text = profile.UserName;
        TrustBox.IsChecked = profile.TrustServerCertificate;
        RememberBox.IsChecked = profile.ProtectedPassword.Length > 0;
        PasswordInput.Password = "";
        if (profile.ProtectedPassword.Length > 0)
        {
            try { PasswordInput.Password = RemoteSqlProfileStore.UnprotectPassword(profile.ProtectedPassword); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                or FormatException or PlatformNotSupportedException)
            {
                RememberBox.IsChecked = false;
                StatusText.Text = "Не удалось расшифровать пароль Windows. Введите его заново.";
            }
        }
        _loading = false;
        UpdateAuthentication();
    }

    private RemoteSqlProfile ReadProfile()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port))
            throw new ArgumentException("Порт должен быть числом от 1 до 65535.");
        var profile = new RemoteSqlProfile
        {
            Name = ProfileNameBox.Text.Trim(),
            Server = ServerBox.Text.Trim(),
            Port = port,
            Database = DatabaseBox.Text.Trim(),
            WindowsAuthentication = AuthBox.SelectedIndex == 0,
            UserName = UserBox.Text.Trim(),
            TrustServerCertificate = TrustBox.IsChecked == true
        };
        profile.Validate();
        return profile;
    }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAuthentication();

    private void UpdateAuthentication()
    {
        if (UserBox is null || PasswordInput is null || RememberBox is null || AuthBox is null) return;
        var sql = AuthBox.SelectedIndex != 0;
        UserBox.IsEnabled = !_busy && sql;
        PasswordInput.IsEnabled = !_busy && sql;
        RememberBox.IsEnabled = !_busy && sql;
    }

    private void TrustBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || TrustBox.IsChecked != true) return;
        if (MessageBox.Show(this,
                "Игнорировать проверку подлинности сертификата SQL Server?\n" +
                "Допустимо только для тестов в доверенной домашней LAN, не для постоянного использования.",
                "Внимание: TLS-сертификат", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            TrustBox.IsChecked = false;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e) =>
        await VerifyAsync();

    private void SetProbeBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in new Control[] { ProfilePicker, DeleteProfileButton, ProfileNameBox,
            ServerBox, PortBox, DatabaseBox, AuthBox, TrustBox, TestButton, ConnectButton, DemoButton })
            control.IsEnabled = !busy;
        UpdateAuthentication();
    }

    private async Task<(RemoteSqlProfile Profile, string Password)?> VerifyAsync()
    {
        if (_busy || _closed) return null;
        RemoteSqlProfile profile;
        try { profile = ReadProfile(); }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return null;
        }
        SetProbeBusy(true);
        StatusText.Text = "Проверка TCP, авторизации и версии схемы...";
        MetricsText.Text = "";
        try
        {
            var password = profile.WindowsAuthentication ? "" : PasswordInput.Password;
            GmcSmeta.Core.StartupTrace.Mark("dialog_sql_probe_begin");
            var result = await RemoteSqlProbe.CheckAsync(profile, password);
            if (_closed) return null;
            GmcSmeta.Core.StartupTrace.Mark(result.Success ? "dialog_sql_probe_success" : "dialog_sql_probe_failed");
            StatusText.Text = result.Message;
            MetricsText.Text = $"TCP: {result.NetworkMilliseconds} мс; SQL и схема: {result.SqlMilliseconds} мс. " +
                               $"Сеть: {(result.NetworkOk ? "ОК" : "ошибка")}; " +
                               $"вход: {(result.LoginOk ? "ОК" : "ошибка")}; " +
                               $"схема: {(result.SchemaOk ? "ОК" : "ошибка")}.";
            return result.Success ? (profile, password) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or OperationCanceledException)
        {
            if (!_closed) StatusText.Text = "Проверка прервана: проверьте параметры и повторите.";
            return null;
        }
        finally
        {
            if (!_closed) SetProbeBusy(false);
            else _busy = false;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        var previouslySelected = ProfilePicker.SelectedItem as RemoteSqlProfile;
        var rememberPassword = RememberBox.IsChecked == true;
        var verified = await VerifyAsync();
        if (_closed || verified is not { } target) return;
        var (profile, password) = target;
        try
        {
            var stored = profile with
            {
                ProtectedPassword = !profile.WindowsAuthentication && rememberPassword
                    ? RemoteSqlProfileStore.ProtectPassword(password) : ""
            };
            var nextProfiles = _profiles.Where(p => !ReferenceEquals(p, previouslySelected) &&
                !p.Name.Equals(stored.Name, StringComparison.OrdinalIgnoreCase)).Append(stored).ToList();
            RemoteSqlProfileStore.Save(nextProfiles);
            _profiles.Clear();
            _profiles.AddRange(nextProfiles);
            // Only ever hand off the connection string in process memory; never serialize it.
            SelectedConnectionString = profile.ConnectionString(password);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            StatusText.Text = "Не удалось сохранить профиль безопасно: " + ex.GetType().Name;
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || ProfilePicker.SelectedItem is not RemoteSqlProfile selected) return;
        if (MessageBox.Show(this, $"Удалить профиль «{selected.Name}»?",
                "Профили", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var nextProfiles = _profiles.Where(p => !ReferenceEquals(p, selected)).ToList();
            RemoteSqlProfileStore.Save(nextProfiles);
            _profiles.Clear();
            _profiles.AddRange(nextProfiles);
            RefreshProfiles(_profiles.FirstOrDefault());
            if (_profiles.Count == 0)
                StatusText.Text = "Профиль удалён. Введите новые параметры.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Не удалось удалить профиль: " + ex.GetType().Name;
        }
    }

    private void DemoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        DemoRequested = true;
        DialogResult = true;
    }
}
