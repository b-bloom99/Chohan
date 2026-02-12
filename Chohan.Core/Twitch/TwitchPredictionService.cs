using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chohan.Core.Twitch;

/// <summary>
/// Twitch Helix Predictions APIを使用して、
/// 予想の作成 → 確定 / キャンセルを自動化するサービス。
/// TwitchOAuthServiceを通じてAPI呼び出しを行い、401時の自動リフレッシュに対応。
/// </summary>
public class TwitchPredictionService : IDisposable
{
    private readonly TwitchOAuthService _oauthService;

    /// <summary>APIが利用可能か（認証済みか）</summary>
    public bool IsConfigured => _oauthService.IsAuthenticated;

    /// <summary>接続状態が変化したときに発火</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>API操作のログイベント</summary>
    public event Action<string>? OperationLog;

    public TwitchPredictionService(TwitchOAuthService oauthService)
    {
        _oauthService = oauthService;
        _oauthService.AuthStateChanged += (isAuth, _) => ConnectionChanged?.Invoke(isAuth);
    }

    // -------------------------------------------------------
    // Prediction 作成
    // -------------------------------------------------------

    /// <summary>
    /// 直近のCreatePredictionで取得したOutcome IDリスト。
    /// Resolve時にGET不要で直接使用できる。
    /// </summary>
    private List<string> _lastOutcomeIds = [];

    /// <summary>
    /// Twitch Predictionを作成する（投票開始）。
    /// Twitch API仕様: title最大45文字、prediction_window: 30～1800秒、outcomes: 2～10個。
    /// </summary>
    /// <param name="title">予想のタイトル（最大45文字、超過分は切り捨て）</param>
    /// <param name="outcomes">選択肢（2つ）</param>
    /// <param name="durationSeconds">投票受付時間（秒）。最小30、最大1800。</param>
    /// <returns>作成されたPredictionのID。失敗時はnull。</returns>
    public async Task<string?> CreatePredictionAsync(
        string title, string[] outcomes, int durationSeconds = 60)
    {
        _lastOutcomeIds.Clear();

        if (!IsConfigured)
        {
            OperationLog?.Invoke("[Twitch] 未認証のためPrediction作成をスキップ");
            return null;
        }

        var broadcasterId = _oauthService.UserId;
        if (string.IsNullOrEmpty(broadcasterId))
        {
            OperationLog?.Invoke("[Twitch] BroadcasterIDが不明です");
            return null;
        }

        // Twitch API仕様: タイトルは最大45文字
        var safeTitle = title.Length > 45 ? title[..45] : title;

        var requestBody = new
        {
            broadcaster_id = broadcasterId,
            title = safeTitle,
            outcomes = outcomes.Select(o => new { title = o.Length > 25 ? o[..25] : o }).ToArray(),
            prediction_window = Math.Clamp(durationSeconds, 30, 1800)
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _oauthService.CallHelixApiAsync(
                HttpMethod.Post, "/predictions", content);

            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                OperationLog?.Invoke($"[Twitch] Prediction作成失敗: {response.StatusCode} - {responseJson}");
                return null;
            }

            var result = JsonSerializer.Deserialize<PredictionApiResponse>(responseJson);
            var prediction = result?.Data?.FirstOrDefault();

            if (prediction == null)
            {
                OperationLog?.Invoke("[Twitch] Predictionレスポンスの解析に失敗");
                return null;
            }

            // Outcome IDを保持（Resolve時にGETなしで使用）
            _lastOutcomeIds = prediction.Outcomes.Select(o => o.Id).ToList();

            OperationLog?.Invoke(
                $"[Twitch] Prediction作成成功: \"{safeTitle}\" (ID: {prediction.Id})");

            return prediction.Id;
        }
        catch (Exception ex)
        {
            OperationLog?.Invoke($"[Twitch] Prediction作成エラー: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------
    // Prediction 確定（結果送信）
    // -------------------------------------------------------

    /// <summary>
    /// Predictionを確定する（勝者を決定し、ポイントを配布）。
    /// Twitch API: PATCH /helix/predictions  status=RESOLVED, winning_outcome_id必須。
    /// </summary>
    /// <param name="predictionId">対象PredictionのID</param>
    /// <param name="winningOutcomeIndex">勝利した選択肢のインデックス (0 or 1)</param>
    public async Task ResolvePredictionAsync(string predictionId, int winningOutcomeIndex)
    {
        if (!IsConfigured) return;

        // 保持済みOutcome IDを優先。無ければGETで取得。
        string? outcomeId = null;
        if (winningOutcomeIndex >= 0 && winningOutcomeIndex < _lastOutcomeIds.Count)
        {
            outcomeId = _lastOutcomeIds[winningOutcomeIndex];
        }
        else
        {
            outcomeId = await GetOutcomeIdAsync(predictionId, winningOutcomeIndex);
        }

        if (string.IsNullOrEmpty(outcomeId))
        {
            OperationLog?.Invoke($"[Twitch] Outcome ID取得失敗 (Prediction: {predictionId})");
            return;
        }

        var requestBody = new
        {
            broadcaster_id = _oauthService.UserId,
            id = predictionId,
            status = "RESOLVED",
            winning_outcome_id = outcomeId
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _oauthService.CallHelixApiAsync(
                HttpMethod.Patch, "/predictions", content);

            if (response.IsSuccessStatusCode)
            {
                OperationLog?.Invoke(
                    $"[Twitch] Prediction確定: {predictionId} → Outcome #{winningOutcomeIndex}");
            }
            else
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                OperationLog?.Invoke(
                    $"[Twitch] Prediction確定失敗: {response.StatusCode} - {errorJson}");
            }
        }
        catch (Exception ex)
        {
            OperationLog?.Invoke($"[Twitch] Prediction確定エラー: {ex.Message}");
        }
    }

    // -------------------------------------------------------
    // Prediction キャンセル
    // -------------------------------------------------------

    /// <summary>Predictionをキャンセルする（ポイントは返還）。</summary>
    public async Task CancelPredictionAsync(string predictionId)
    {
        if (!IsConfigured) return;

        var requestBody = new
        {
            broadcaster_id = _oauthService.UserId,
            id = predictionId,
            status = "CANCELED"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _oauthService.CallHelixApiAsync(
                HttpMethod.Patch, "/predictions", content);

            if (response.IsSuccessStatusCode)
            {
                OperationLog?.Invoke($"[Twitch] Predictionキャンセル: {predictionId}");
            }
            else
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                OperationLog?.Invoke(
                    $"[Twitch] キャンセル失敗: {response.StatusCode} - {errorJson}");
            }
        }
        catch (Exception ex)
        {
            OperationLog?.Invoke($"[Twitch] キャンセルエラー: {ex.Message}");
        }
    }

    // -------------------------------------------------------
    // Prediction ロック（投票受付終了）
    // -------------------------------------------------------

    /// <summary>投票受付を締め切る（結果はまだ確定しない）。</summary>
    public async Task LockPredictionAsync(string predictionId)
    {
        if (!IsConfigured) return;

        var requestBody = new
        {
            broadcaster_id = _oauthService.UserId,
            id = predictionId,
            status = "LOCKED"
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _oauthService.CallHelixApiAsync(
                HttpMethod.Patch, "/predictions", content);

            if (response.IsSuccessStatusCode)
            {
                OperationLog?.Invoke($"[Twitch] Prediction投票締切: {predictionId}");
            }
        }
        catch (Exception ex)
        {
            OperationLog?.Invoke($"[Twitch] 締切エラー: {ex.Message}");
        }
    }

    // -------------------------------------------------------
    // ヘルパー
    // -------------------------------------------------------

    /// <summary>PredictionのOutcome IDを取得する。</summary>
    private async Task<string?> GetOutcomeIdAsync(string predictionId, int outcomeIndex)
    {
        try
        {
            var response = await _oauthService.CallHelixApiAsync(
                HttpMethod.Get,
                $"/predictions?broadcaster_id={_oauthService.UserId}&id={predictionId}");

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PredictionApiResponse>(json);
            var prediction = result?.Data?.FirstOrDefault();

            if (prediction?.Outcomes == null || outcomeIndex >= prediction.Outcomes.Count)
                return null;

            return prediction.Outcomes[outcomeIndex].Id;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // OAuthServiceのDisposeは呼び出し元の責任
    }
}

// -------------------------------------------------------
// Helix Predictions API レスポンスモデル
// -------------------------------------------------------

internal class PredictionApiResponse
{
    [JsonPropertyName("data")]
    public List<PredictionData>? Data { get; set; }
}

internal class PredictionData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("broadcaster_id")]
    public string BroadcasterId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("outcomes")]
    public List<PredictionOutcome> Outcomes { get; set; } = [];

    [JsonPropertyName("prediction_window")]
    public int PredictionWindow { get; set; }
}

internal class PredictionOutcome
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("users")]
    public int Users { get; set; }

    [JsonPropertyName("channel_points")]
    public int ChannelPoints { get; set; }
}
