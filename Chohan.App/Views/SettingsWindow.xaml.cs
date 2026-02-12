using System.Windows;
using System.Windows.Controls;
using Chohan.App.ViewModels;

namespace Chohan.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    public SettingsWindow()
    {
        InitializeComponent();

        RoiEditor.RoiChanged += OnRoiChanged;

        Loaded += (_, _) =>
        {
            if (Vm == null) return;

            Vm.RequestProfileName = existingNames =>
            {
                var dlg = new InputDialog { Owner = this, ExistingNames = existingNames };
                dlg.SetPrompt("新しいプロフィール名を入力してください：");
                return dlg.ShowDialog() == true ? dlg.InputText : null;
            };

            Vm.ConfirmDeleteProfile = profileName =>
            {
                var result = MessageBox.Show(
                    $"プロフィール「{profileName}」を削除しますか？\nテンプレート画像と設定が完全に削除されます。",
                    "Chohan - 削除確認",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                return result == MessageBoxResult.Yes;
            };

            Vm.RequestRenameProfile = (currentName, existingNames) =>
            {
                var dlg = new InputDialog { Owner = this, ExistingNames = existingNames };
                dlg.SetPrompt($"「{currentName}」の新しい名前を入力してください：");
                dlg.SetInitialText(currentName);
                return dlg.ShowDialog() == true ? dlg.InputText : null;
            };

            if (!string.IsNullOrEmpty(Vm.TwitchClientSecret))
                ClientSecretBox.Password = Vm.TwitchClientSecret;
        };
    }

    private void OnRoiChanged(Rect canvasRoi)
    {
        if (Vm != null) Vm.CanvasRoi = canvasRoi;
    }

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var source = Vm.PreviewImage;
        if (source == null)
        {
            MessageBox.Show("カメラ映像がありません。先にカメラを起動してください。",
                "Chohan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var frameRoi = RoiEditor.GetRoiInFrameCoordinates(source.PixelWidth, source.PixelHeight);
        if (frameRoi.IsEmpty || frameRoi.Width < 4 || frameRoi.Height < 4)
        {
            MessageBox.Show("ROI（範囲）を先にドラッグで指定してください。",
                "Chohan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Vm.RegisterCurrentRoi(frameRoi);
    }

    private void TriggerSelect_Start(object sender, RoutedEventArgs e) => SelectTrigger("start");
    private void TriggerSelect_Win(object sender, RoutedEventArgs e) => SelectTrigger("win");
    private void TriggerSelect_Lose(object sender, RoutedEventArgs e) => SelectTrigger("lose");
    private void SelectTrigger(string key)
    {
        if (Vm != null) { Vm.SelectedTriggerKey = key; RoiEditor.ClearRoi(); }
    }

    private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.TwitchClientSecret = ((PasswordBox)sender).Password;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "このプロフィールの全履歴を削除しますか？\nこの操作は元に戻せません。",
            "Chohan - 履歴クリア",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) Vm?.ClearHistory();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, EventArgs e)
    {
        RoiEditor.RoiChanged -= OnRoiChanged;
        if (DataContext is IDisposable disposable) disposable.Dispose();
    }
}
