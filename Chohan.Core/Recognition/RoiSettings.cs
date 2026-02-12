using OpenCvSharp;

namespace Chohan.Core.Recognition;

/// <summary>
/// テンプレートマッチングのROI（関心領域）と閾値を保持する設定クラス。
/// </summary>
public class RoiSettings
{
    /// <summary>対象画像の矩形領域 (フレーム座標系)</summary>
    public Rect Region { get; set; }

    /// <summary>一致率の閾値 (0.0 ～ 1.0)。これを超えたらトリガー発火</summary>
    public double Threshold { get; set; } = 0.80;

    /// <summary>テンプレート画像のファイルパス</summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>テンプレート画像 (キャッシュ用、SetTemplateで設定)</summary>
    public Mat? TemplateImage { get; private set; }

    /// <summary>テンプレート画像をファイルからロードする</summary>
    public void LoadTemplate()
    {
        TemplateImage?.Dispose();
        TemplateImage = null;

        if (!string.IsNullOrEmpty(TemplatePath) && File.Exists(TemplatePath))
        {
            TemplateImage = Cv2.ImRead(TemplatePath, ImreadModes.Color);
        }
    }

    /// <summary>テンプレート画像を直接設定する</summary>
    public void SetTemplate(Mat template)
    {
        TemplateImage?.Dispose();
        TemplateImage = template.Clone();
    }
}
