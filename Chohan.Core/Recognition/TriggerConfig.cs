using OpenCvSharp;

namespace Chohan.Core.Recognition;

/// <summary>
/// 1つのトリガー（開始/勝利/敗北）に対する設定をまとめて保持するクラス。
/// ROI座標、テンプレート画像パス、閾値を1つの単位として管理する。
/// </summary>
public class TriggerConfig : IDisposable
{
    /// <summary>トリガー名 (start / win / lose)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>表示用ラベル</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>ROI矩形領域（フレーム実座標系 = カメラ解像度基準）</summary>
    public Rect RoiRect { get; set; }

    /// <summary>一致率の閾値 (0.0 ～ 1.0)</summary>
    public double Threshold { get; set; } = 0.80;

    /// <summary>テンプレート画像の保存ファイルパス</summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>テンプレート画像（メモリ上のキャッシュ）</summary>
    public Mat? TemplateImage { get; private set; }

    /// <summary>テンプレートが登録済みか</summary>
    public bool HasTemplate => TemplateImage != null && !TemplateImage.Empty();

    /// <summary>ROIが設定済みか</summary>
    public bool HasRoi => RoiRect.Width > 0 && RoiRect.Height > 0;

    /// <summary>テンプレート画像をファイルからロードする</summary>
    public bool LoadTemplate()
    {
        TemplateImage?.Dispose();
        TemplateImage = null;

        if (!string.IsNullOrEmpty(TemplatePath) && File.Exists(TemplatePath))
        {
            TemplateImage = Cv2.ImRead(TemplatePath, ImreadModes.Color);
            return TemplateImage != null && !TemplateImage.Empty();
        }
        return false;
    }

    /// <summary>テンプレート画像を直接設定し、ファイルにも保存する</summary>
    public void SetTemplate(Mat template, string savePath)
    {
        TemplateImage?.Dispose();
        TemplateImage = template.Clone();
        TemplatePath = savePath;

        // ディレクトリがなければ作成
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Cv2.ImWrite(savePath, TemplateImage);
    }

    /// <summary>フレームからROI領域を切り出してテンプレートとして登録する</summary>
    public void CaptureFromFrame(Mat frame, Rect roi, string savePath)
    {
        RoiRect = roi;

        // ROIをフレームサイズにクリップ
        var clipped = ClipRect(roi, frame.Size());
        if (clipped.Width <= 0 || clipped.Height <= 0) return;

        using var roiMat = new Mat(frame, clipped);
        SetTemplate(roiMat, savePath);
    }

    /// <summary>RoiSettingsに変換する（既存のTemplateMatchingEngineとの互換用）</summary>
    public RoiSettings ToRoiSettings()
    {
        var settings = new RoiSettings
        {
            Region = RoiRect,
            Threshold = Threshold,
            TemplatePath = TemplatePath
        };
        if (TemplateImage != null)
            settings.SetTemplate(TemplateImage);
        return settings;
    }

    private static Rect ClipRect(Rect rect, Size frameSize)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(frameSize.Width, rect.X + rect.Width);
        int bottom = Math.Min(frameSize.Height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    public void Dispose()
    {
        TemplateImage?.Dispose();
        TemplateImage = null;
    }
}
