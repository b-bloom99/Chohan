using System.IO;
using System.Windows;

namespace Chohan.App.Views;

/// <summary>
/// 汎用テキスト入力ダイアログ。
/// プロフィール名の新規作成・リネーム時に使用。
/// </summary>
public partial class InputDialog : Window
{
    /// <summary>入力されたテキスト</summary>
    public string InputText { get; private set; } = string.Empty;

    /// <summary>既存の名前一覧（重複チェック用）</summary>
    public List<string> ExistingNames { get; set; } = [];

    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    /// <summary>プロンプト文を設定</summary>
    public void SetPrompt(string prompt)
    {
        PromptText.Text = prompt;
    }

    /// <summary>初期値を設定</summary>
    public void SetInitialText(string text)
    {
        InputTextBox.Text = text;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var text = InputTextBox.Text?.Trim() ?? string.Empty;

        // バリデーション
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowError("名前を入力してください。");
            return;
        }

        if (text.Length > 64)
        {
            ShowError("名前は64文字以内にしてください。");
            return;
        }

        // ファイルシステムで使えない文字チェック
        var invalidChars = Path.GetInvalidFileNameChars();
        if (text.Any(c => invalidChars.Contains(c)))
        {
            ShowError("ファイル名に使えない文字が含まれています。");
            return;
        }

        // 重複チェック
        if (ExistingNames.Any(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"「{text}」は既に存在します。別の名前を入力してください。");
            return;
        }

        InputText = text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }
}
