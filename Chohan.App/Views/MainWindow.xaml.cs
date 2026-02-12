using System.Windows;
using System.Windows.Input;
using Chohan.App.ViewModels;

namespace Chohan.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    /// <summary>ウィンドウ読み込み時に保存済み位置を復元</summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var (x, y, w, h) = vm.GetSavedWindowBounds();
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                Left = x;
                Top = y;
            }
            if (w > 0) Width = w;
            if (h > 0) Height = h;
        }
    }

    /// <summary>ウィンドウのドラッグ移動</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    /// <summary>終了メニュー</summary>
    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>ウィンドウ閉じ時 — 位置保存とクリーンアップ</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // ウィンドウ位置を保存
            vm.SaveWindowBounds(Left, Top, Width, Height);
            vm.Dispose();
        }

        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
