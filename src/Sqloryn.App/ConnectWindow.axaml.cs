using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqloryn.Core;

namespace Sqloryn;

public partial class ConnectWindow : Window
{
    public ConnectionOptions? Result { get; private set; }

    public ConnectWindow()
    {
        InitializeComponent();
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    void OnConnect(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out var port))
                throw new ArgumentException("Port must be an integer.");
            var options = new ConnectionOptions
            {
                Server = ServerBox.Text?.Trim() ?? "",
                Port = port,
                Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? "master" : DatabaseBox.Text.Trim(),
                UserId = UserBox.Text?.Trim() ?? "",
                Password = PasswordBox.Text ?? "",
                Encrypt = true,
                TrustServerCertificate = TrustBox.IsChecked == true,
                ServerCertificatePath = string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim()
            };
            options.Validate();
            Result = options;
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
    }
}
