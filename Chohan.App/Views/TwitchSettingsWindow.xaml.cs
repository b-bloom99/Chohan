using System.Windows;
using System.Windows.Controls;
using Chohan.App.ViewModels;

namespace Chohan.App.Views;

public partial class TwitchSettingsWindow : Window
{
    private TwitchSettingsViewModel? Vm => DataContext as TwitchSettingsViewModel;

    public TwitchSettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBoxはバインディング非対応のため、
    /// PasswordChangedイベントで手動同期する。
    /// </summary>
    private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm != null && sender is PasswordBox pb)
        {
            Vm.ClientSecret = pb.Password;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
        base.OnClosed(e);
    }
}
