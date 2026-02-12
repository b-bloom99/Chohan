using System.Text.Json.Serialization;

namespace Chohan.Core.Config;

/// <summary>
/// ゲーム別プロフィール (Profiles/*.json)。
/// トリガーのROI座標・閾値・テンプレート画像パスと投票設定を保持する。
/// </summary>
public class GameProfile
{
    /// <summary>プロフィール名（ファイル名ベース、拡張子なし）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    /// <summary>表示用の説明</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    // -------------------------------------------------------
    // トリガー設定
    // -------------------------------------------------------

    /// <summary>開始トリガー</summary>
    [JsonPropertyName("trigger_start")]
    public TriggerEntry TriggerStart { get; set; } = new();

    /// <summary>勝利トリガー</summary>
    [JsonPropertyName("trigger_win")]
    public TriggerEntry TriggerWin { get; set; } = new();

    /// <summary>敗北トリガー</summary>
    [JsonPropertyName("trigger_lose")]
    public TriggerEntry TriggerLose { get; set; } = new();

    // -------------------------------------------------------
    // 投票設定
    // -------------------------------------------------------

    /// <summary>投票のデフォルトタイトル</summary>
    [JsonPropertyName("prediction_title")]
    public string PredictionTitle { get; set; } = "次の結果は？";

    /// <summary>選択肢1のラベル</summary>
    [JsonPropertyName("outcome_win_label")]
    public string OutcomeWinLabel { get; set; } = "勝利";

    /// <summary>選択肢2のラベル</summary>
    [JsonPropertyName("outcome_lose_label")]
    public string OutcomeLoseLabel { get; set; } = "敗北";

    /// <summary>投票受付時間（秒）</summary>
    [JsonPropertyName("prediction_duration_seconds")]
    public int PredictionDurationSeconds { get; set; } = 60;
}

/// <summary>
/// 1つのトリガー（開始/勝利/敗北）のJSON保存用データ。
/// </summary>
public class TriggerEntry
{
    /// <summary>ROI矩形の X 座標</summary>
    [JsonPropertyName("roi_x")]
    public int RoiX { get; set; }

    /// <summary>ROI矩形の Y 座標</summary>
    [JsonPropertyName("roi_y")]
    public int RoiY { get; set; }

    /// <summary>ROI矩形の幅</summary>
    [JsonPropertyName("roi_width")]
    public int RoiWidth { get; set; }

    /// <summary>ROI矩形の高さ</summary>
    [JsonPropertyName("roi_height")]
    public int RoiHeight { get; set; }

    /// <summary>一致率閾値 (0.0～1.0)</summary>
    [JsonPropertyName("threshold")]
    public double Threshold { get; set; } = 0.80;

    /// <summary>テンプレート画像のパス (Templates/からの相対パス)</summary>
    [JsonPropertyName("template_path")]
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>有効なROIが設定されているか</summary>
    [JsonIgnore]
    public bool HasRoi => RoiWidth > 0 && RoiHeight > 0;
}
