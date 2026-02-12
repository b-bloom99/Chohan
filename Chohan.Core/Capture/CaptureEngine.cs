using System.Management;
using OpenCvSharp;

namespace Chohan.Core.Capture;

/// <summary>
/// OpenCvSharpのVideoCaptureを使用してカメラ映像をキャプチャするエンジン。
/// OBS仮想カメラ等のデバイスにも透過的に対応。
/// 
/// デバイス列挙はWMI (Win32_PnPEntity) で実名を取得。
/// キャプチャはバックエンド自動選択（ANY）で開く。
/// Start/Stopは _lifecycleLock で排他制御し、停止時はタスク完了を確実に待機する。
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    // 排他制御用のロックオブジェクト
    private readonly object _frameLock = new();
    private readonly object _lifecycleLock = new(); 

    private Mat? _latestFrame;
    private volatile bool _isRunning;
    private bool _disposed;

    /// <summary>新しいフレームが取得されたときに発火するイベント</summary>
    public event Action<Mat>? FrameCaptured;

    /// <summary>キャプチャ中かどうか</summary>
    public bool IsRunning => _isRunning;

    // -------------------------------------------------------
    // デバイス列挙
    // -------------------------------------------------------

    /// <summary>
    /// 使用可能なビデオキャプチャデバイスの情報を返す。
    /// WMIで実デバイス名を取得し、VideoCaptureで検証する。
    /// </summary>
    public static List<CameraDeviceInfo> EnumerateVideoDevices()
    {
        var devices = new List<CameraDeviceInfo>();
        var wmiNames = GetVideoDeviceNamesFromWmi();
        int probeCount = wmiNames.Count > 0 ? wmiNames.Count : 10;

        for (int i = 0; i < probeCount; i++)
        {
            try
            {
                // バックエンドをANY（自動選択）にして安定性を確保
                using var cap = new VideoCapture(i, VideoCaptureAPIs.ANY);
                if (cap.IsOpened())
                {
                    var width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                    var height = (int)cap.Get(VideoCaptureProperties.FrameHeight);

                    var displayName = (i < wmiNames.Count && !string.IsNullOrEmpty(wmiNames[i]))
                        ? $"{wmiNames[i]} ({width}x{height})"
                        : $"Camera {i} ({width}x{height})";

                    devices.Add(new CameraDeviceInfo
                    {
                        Index = i,
                        Name = displayName,
                        Width = width,
                        Height = height
                    });
                }
            }
            catch
            {
                // プローブ失敗は安全に無視
            }
        }
        return devices;
    }

    /// <summary>
    /// WMI (Win32_PnPEntity) からビデオ入力デバイスの名前一覧を取得する。
    /// </summary>
    private static List<string> GetVideoDeviceNamesFromWmi()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Caption"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }
        catch { }
        return names;
    }

    // -------------------------------------------------------
    // キャプチャ開始 / 停止
    // -------------------------------------------------------

    /// <summary>
    /// 指定デバイスインデックスでキャプチャを開始する。
    /// Start/Stopは _lifecycleLock で排他制御される。
    /// </summary>
    public void Start(int deviceIndex, int width = 640, int height = 480)
    {
        // StartとStopが同時に走らないようにロック
        lock (_lifecycleLock)
        {
            if (_isRunning) StopInternal(); // 既に動いている場合は停止

            _captureCts = new CancellationTokenSource();
            var ct = _captureCts.Token;

            _captureTask = Task.Run(() =>
            {
                // バックエンドをANYに設定して開始
                using var capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.ANY);
                
                if (width > 0) capture.FrameWidth = width;
                if (height > 0) capture.FrameHeight = height;

                if (!capture.IsOpened()) return;

                _isRunning = true;

                // メモリ安全性のためループ内でMatを生成（AccessViolation回避）
                using var frame = new Mat();

                while (_isRunning && !ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!capture.IsOpened()) break;

                        // フレーム読み込み
                        if (!capture.Read(frame) || frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // 最新フレームをクローンして保持
                        lock (_frameLock)
                        {
                            _latestFrame?.Dispose();
                            _latestFrame = frame.Clone();
                        }

                        // イベント通知
                        var handlers = FrameCaptured;
                        if (handlers != null)
                        {
                            foreach (Action<Mat> handler in handlers.GetInvocationList())
                            {
                                try 
                                { 
                                    handler(frame.Clone()); 
                                } 
                                catch { }
                            }
                        }

                        Thread.Sleep(33); // 約30fpsに制限
                    }
                    catch (OperationCanceledException) { break; }
                    catch { Thread.Sleep(50); }
                }

                _isRunning = false;
                // usingを抜ける際にcapture.Dispose()が呼ばれる
            }, ct);
        }
    }

    /// <summary>
    /// CameraDeviceInfo を指定してキャプチャを開始する。
    /// </summary>
    public void Start(CameraDeviceInfo device, int width = 640, int height = 480)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));
        Start(device.Index, width, height);
    }

    /// <summary>
    /// キャプチャを停止し、リソースを解放する。
    /// タスクが完全に終了するまでブロックする。
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopInternal();
        }
    }

    /// <summary>
    /// 停止の内部実装。_lifecycleLock 保持下で呼ぶこと。
    /// </summary>
    private void StopInternal()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _captureCts?.Cancel();

        try
        {
            // タスクが完全に終了するまで待機（最大3秒）
            if (_captureTask != null && !_captureTask.IsCompleted)
            {
                _captureTask.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch { /* タイムアウト等は無視 */ }
        finally
        {
            _captureCts?.Dispose();
            _captureCts = null;
            _captureTask = null;
        }

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
    }

    // -------------------------------------------------------
    // フレーム取得
    // -------------------------------------------------------

    /// <summary>
    /// 最新フレームのコピーを取得する（スレッドセーフ）。
    /// </summary>
    public Mat? GetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestFrame?.Clone();
        }
    }

    // -------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dispose時も安全にStopを呼ぶ
        Stop();
    }
}

/// <summary>
/// カメラデバイス情報。EnumerateVideoDevicesの戻り値。
/// </summary>
public class CameraDeviceInfo
{
    /// <summary>OpenCvSharp用のデバイスインデックス</summary>
    public int Index { get; set; }

    /// <summary>表示用デバイス名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>検出時の解像度幅</summary>
    public int Width { get; set; }

    /// <summary>検出時の解像度高さ</summary>
    public int Height { get; set; }

    public override string ToString() => Name;
}