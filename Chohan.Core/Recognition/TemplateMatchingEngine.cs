using OpenCvSharp;

namespace Chohan.Core.Recognition;

/// <summary>
/// テンプレートマッチングによる画像認識エンジン。
/// 指定ROI内でテンプレートとの一致率を算出し、閾値超過を判定する。
/// </summary>
public sealed class TemplateMatchingEngine : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 指定フレームのROI領域に対してテンプレートマッチングを実行し、一致率を返す。
    /// </summary>
    /// <param name="frame">入力フレーム (BGR)</param>
    /// <param name="settings">ROIとテンプレートの設定</param>
    /// <returns>一致率 (0.0～1.0)。テンプレート未設定時は0.0</returns>
    public double Match(Mat frame, RoiSettings settings)
    {
        if (frame.Empty() || settings.TemplateImage == null || settings.TemplateImage.Empty())
            return 0.0;

        // ROIの切り出し（フレーム範囲にクリップ）
        var roi = ClipRoi(settings.Region, frame.Size());
        if (roi.Width <= 0 || roi.Height <= 0)
            return 0.0;

        using var roiMat = new Mat(frame, roi);

        // テンプレートをROIサイズに合わせてリサイズ（必要な場合）
        using var template = PrepareTemplate(settings.TemplateImage, roiMat.Size());
        if (template.Empty())
            return 0.0;

        // テンプレートがROIより大きい場合はマッチング不可
        if (template.Rows > roiMat.Rows || template.Cols > roiMat.Cols)
            return 0.0;

        // テンプレートマッチング実行
        using var result = new Mat();
        Cv2.MatchTemplate(roiMat, template, result, TemplateMatchModes.CCoeffNormed);

        // 最大一致率を取得
        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

        return Math.Clamp(maxVal, 0.0, 1.0);
    }

    /// <summary>
    /// 一致率が閾値を超えているか判定する。
    /// </summary>
    public bool IsMatched(Mat frame, RoiSettings settings)
    {
        var score = Match(frame, settings);
        return score >= settings.Threshold;
    }

    /// <summary>
    /// 複数のトリガーに対して一括でマッチングを行い、結果を返す。
    /// </summary>
    public Dictionary<string, (double Score, bool Matched)> MatchAll(
        Mat frame, Dictionary<string, RoiSettings> triggers)
    {
        var results = new Dictionary<string, (double, bool)>();
        foreach (var (name, settings) in triggers)
        {
            var score = Match(frame, settings);
            results[name] = (score, score >= settings.Threshold);
        }
        return results;
    }

    // -------------------------------------------------------
    // ヘルパー
    // -------------------------------------------------------

    /// <summary>ROIをフレームサイズに収まるようクリップ</summary>
    private static Rect ClipRoi(Rect roi, Size frameSize)
    {
        int x = Math.Max(0, roi.X);
        int y = Math.Max(0, roi.Y);
        int right = Math.Min(frameSize.Width, roi.X + roi.Width);
        int bottom = Math.Min(frameSize.Height, roi.Y + roi.Height);
        return new Rect(x, y, right - x, bottom - y);
    }

    /// <summary>テンプレートをROI内に収まるよう必要に応じリサイズ</summary>
    private static Mat PrepareTemplate(Mat template, Size targetSize)
    {
        // テンプレートがROIと同サイズ、または小さい場合はそのまま返す
        if (template.Cols <= targetSize.Width && template.Rows <= targetSize.Height)
            return template.Clone();

        // アスペクト比を保ちつつリサイズ
        double scaleX = (double)targetSize.Width / template.Cols;
        double scaleY = (double)targetSize.Height / template.Rows;
        double scale = Math.Min(scaleX, scaleY);

        var newSize = new Size((int)(template.Cols * scale), (int)(template.Rows * scale));
        var resized = new Mat();
        Cv2.Resize(template, resized, newSize, interpolation: InterpolationFlags.Area);
        return resized;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
