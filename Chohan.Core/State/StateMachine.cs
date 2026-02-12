namespace Chohan.Core.State;

/// <summary>
/// 画像認識結果に基づく状態遷移マシン。
/// 
/// 通常モード:    Idle → (Start検知) → Voting → (Win/Lose検知) → Resolved → (遅延) → Idle
/// 常時投票モード: Voting → (Win/Lose検知) → Resolved → (遅延) → Voting（ループ）
/// 
/// IsAlwaysVotingMode = true の場合:
///   - Start() でいきなり Voting に遷移
///   - Resolved 後の自動リセットも Voting に戻る
///   - Idle状態を経由しない
/// </summary>
public class StateMachine
{
    private GameState _currentState = GameState.Stopped;
    private readonly object _lock = new();
    private bool _alwaysVotingMode;
    private int _resolvedDelaySeconds = 5;

    /// <summary>現在の状態</summary>
    public GameState CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    /// <summary>
    /// 常時投票モード。trueの場合、開始トリガーをスキップし、
    /// 結果確定後に自動で投票状態に戻る。
    /// </summary>
    public bool IsAlwaysVotingMode
    {
        get => _alwaysVotingMode;
        set
        {
            _alwaysVotingMode = value;
            // 動作中に切り替わった場合、Idleなら即Votingへ
            lock (_lock)
            {
                if (value && _currentState == GameState.Idle)
                    TransitionTo(GameState.Voting, MatchResult.None);
            }
        }
    }

    /// <summary>結果確定後、次の状態に戻るまでの待機秒数</summary>
    public int ResolvedDelaySeconds
    {
        get => _resolvedDelaySeconds;
        set => _resolvedDelaySeconds = Math.Max(1, value);
    }

    /// <summary>状態が変化したときに発火するイベント</summary>
    public event Action<GameState, MatchResult>? StateChanged;

    // -------------------------------------------------------
    // 開始 / 停止
    // -------------------------------------------------------

    /// <summary>監視を開始する</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_currentState == GameState.Stopped || _currentState == GameState.Resolved)
            {
                // 常時投票モードなら直接Votingへ
                var targetState = _alwaysVotingMode ? GameState.Voting : GameState.Idle;
                TransitionTo(targetState, MatchResult.None);
            }
        }
    }

    /// <summary>監視を停止する</summary>
    public void Stop()
    {
        lock (_lock)
        {
            TransitionTo(GameState.Stopped, MatchResult.None);
        }
    }

    // -------------------------------------------------------
    // マッチング結果フィード
    // -------------------------------------------------------

    /// <summary>マッチング結果をフィードし、状態遷移を判定する。</summary>
    public void Feed(MatchResult result)
    {
        lock (_lock)
        {
            switch (_currentState)
            {
                // --- 通常モード: Idle → Voting ---
                case GameState.Idle when result == MatchResult.Start && !_alwaysVotingMode:
                    TransitionTo(GameState.Voting, result);
                    break;

                // --- 勝利検知 ---
                case GameState.Voting when result == MatchResult.Win:
                    TransitionTo(GameState.Resolved, result);
                    _ = ResetAfterDelay();
                    break;

                // --- 敗北検知 ---
                case GameState.Voting when result == MatchResult.Lose:
                    TransitionTo(GameState.Resolved, result);
                    _ = ResetAfterDelay();
                    break;
            }
        }
    }

    // -------------------------------------------------------
    // リセット
    // -------------------------------------------------------

    /// <summary>手動でリセットする</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_currentState == GameState.Stopped) return;
            var target = _alwaysVotingMode ? GameState.Voting : GameState.Idle;
            TransitionTo(target, MatchResult.None);
        }
    }

    // -------------------------------------------------------
    // 内部
    // -------------------------------------------------------

    private void TransitionTo(GameState newState, MatchResult result)
    {
        if (_currentState == newState) return;
        _currentState = newState;
        StateChanged?.Invoke(newState, result);
    }

    private async Task ResetAfterDelay()
    {
        await Task.Delay(TimeSpan.FromSeconds(_resolvedDelaySeconds));
        Reset();
    }
}
