using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Chohan.App.Controls;

/// <summary>
/// OBS仮想カメラのライブ映像上でROI矩形をドラッグ指定するカスタムコントロール。
/// 
/// 機能:
/// - マウスドラッグで新規矩形描画
/// - 四隅ハンドルによるリサイズ
/// - 矩形内ドラッグによる移動
/// - マウス周辺200%拡大プレビュー
/// - 一致率に応じた枠色変更（赤/緑）
/// </summary>
public class RoiEditorControl : UserControl
{
    // -------------------------------------------------------
    // 定数
    // -------------------------------------------------------
    private const double HandleSize = 8;
    private const double MagnifierSize = 120;
    private const double MagnifierZoom = 2.0;

    // -------------------------------------------------------
    // UIパーツ
    // -------------------------------------------------------
    private readonly Image _backgroundImage;
    private readonly Canvas _overlayCanvas;
    private readonly Rectangle _roiRect;
    private readonly Rectangle[] _handles = new Rectangle[4]; // TL, TR, BL, BR
    private readonly Border _magnifier;
    private readonly Image _magnifierImage;

    // -------------------------------------------------------
    // 状態管理
    // -------------------------------------------------------
    private enum DragMode { None, DrawNew, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }

    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private Rect _roiBefore; // ドラッグ開始時のROI
    private Rect _currentRoi; // 現在のROI (Canvas座標系)
    private bool _hasRoi;

    // -------------------------------------------------------
    // 依存プロパティ
    // -------------------------------------------------------

    /// <summary>表示するカメラ映像のBitmapSource</summary>
    public static readonly DependencyProperty SourceImageProperty =
        DependencyProperty.Register(nameof(SourceImage), typeof(BitmapSource), typeof(RoiEditorControl),
            new PropertyMetadata(null, OnSourceImageChanged));

    public BitmapSource? SourceImage
    {
        get => (BitmapSource?)GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    /// <summary>現在の一致率 (0.0～1.0) — 枠色の切り替えに使用</summary>
    public static readonly DependencyProperty MatchScoreProperty =
        DependencyProperty.Register(nameof(MatchScore), typeof(double), typeof(RoiEditorControl),
            new PropertyMetadata(0.0, OnMatchScoreChanged));

    public double MatchScore
    {
        get => (double)GetValue(MatchScoreProperty);
        set => SetValue(MatchScoreProperty, value);
    }

    /// <summary>閾値 (0.0～1.0) — 枠色判定に使用</summary>
    public static readonly DependencyProperty ThresholdProperty =
        DependencyProperty.Register(nameof(Threshold), typeof(double), typeof(RoiEditorControl),
            new PropertyMetadata(0.8, OnMatchScoreChanged));

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    /// <summary>拡大鏡を表示するか</summary>
    public static readonly DependencyProperty ShowMagnifierProperty =
        DependencyProperty.Register(nameof(ShowMagnifier), typeof(bool), typeof(RoiEditorControl),
            new PropertyMetadata(true));

    public bool ShowMagnifier
    {
        get => (bool)GetValue(ShowMagnifierProperty);
        set => SetValue(ShowMagnifierProperty, value);
    }

    // -------------------------------------------------------
    // イベント
    // -------------------------------------------------------

    /// <summary>ROIが変更されたときに発火（Canvas座標系のRect）</summary>
    public event Action<Rect>? RoiChanged;

    // -------------------------------------------------------
    // コンストラクタ
    // -------------------------------------------------------

    public RoiEditorControl()
    {
        // --- レイアウト構築 ---
        var grid = new Grid();

        // 背景画像
        _backgroundImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(_backgroundImage);

        // オーバーレイCanvas
        _overlayCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        grid.Children.Add(_overlayCanvas);

        // ROI矩形
        _roiRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 69, 58)), // 赤
            StrokeThickness = 2,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            Visibility = Visibility.Collapsed
        };
        _overlayCanvas.Children.Add(_roiRect);

        // リサイズハンドル (4隅)
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                Cursor = i switch
                {
                    0 => Cursors.SizeNWSE, // TL
                    1 => Cursors.SizeNESW, // TR
                    2 => Cursors.SizeNESW, // BL
                    3 => Cursors.SizeNWSE, // BR
                    _ => Cursors.Arrow
                }
            };
            _overlayCanvas.Children.Add(_handles[i]);
        }

        // 拡大鏡
        _magnifierImage = new Image { Stretch = Stretch.None };
        _magnifier = new Border
        {
            Width = MagnifierSize,
            Height = MagnifierSize,
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Black,
            Child = new Viewbox { Child = _magnifierImage, Stretch = Stretch.Uniform },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _overlayCanvas.Children.Add(_magnifier);

        // --- イベント ---
        _overlayCanvas.MouseLeftButtonDown += OnMouseDown;
        _overlayCanvas.MouseMove += OnMouseMove;
        _overlayCanvas.MouseLeftButtonUp += OnMouseUp;
        _overlayCanvas.MouseLeave += OnMouseLeave;

        Content = grid;
    }

    // -------------------------------------------------------
    // プロパティ変更コールバック
    // -------------------------------------------------------

    private static void OnSourceImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RoiEditorControl ctrl && e.NewValue is BitmapSource bmp)
        {
            ctrl._backgroundImage.Source = bmp;
        }
    }

    private static void OnMatchScoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RoiEditorControl ctrl)
        {
            ctrl.UpdateRoiColor();
        }
    }

    // -------------------------------------------------------
    // 外部API
    // -------------------------------------------------------

    /// <summary>
    /// ROIをCanvas座標系で設定する（外部から初期値を与える場合）
    /// </summary>
    public void SetRoi(Rect canvasRect)
    {
        _currentRoi = canvasRect;
        _hasRoi = true;
        UpdateRoiVisuals();
    }

    /// <summary>
    /// 現在のROIをフレームの実座標系（ピクセル座標）に変換して返す。
    /// Canvas上の座標 → カメラ解像度の座標 への変換。
    /// </summary>
    public Rect GetRoiInFrameCoordinates(int frameWidth, int frameHeight)
    {
        if (!_hasRoi || SourceImage == null) return Rect.Empty;

        // 画像がCanvas内でどのようにレンダリングされているか計算
        var canvasW = _overlayCanvas.ActualWidth;
        var canvasH = _overlayCanvas.ActualHeight;
        var imgW = SourceImage.PixelWidth;
        var imgH = SourceImage.PixelHeight;

        GetImageRenderRect(canvasW, canvasH, imgW, imgH,
            out double renderX, out double renderY, out double renderW, out double renderH);

        if (renderW <= 0 || renderH <= 0) return Rect.Empty;

        // Canvas座標 → 画像ピクセル座標
        double scaleX = (double)frameWidth / renderW;
        double scaleY = (double)frameHeight / renderH;

        double x = (_currentRoi.X - renderX) * scaleX;
        double y = (_currentRoi.Y - renderY) * scaleY;
        double w = _currentRoi.Width * scaleX;
        double h = _currentRoi.Height * scaleY;

        // クリップ
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        w = Math.Min(w, frameWidth - x);
        h = Math.Min(h, frameHeight - y);

        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>ROIをクリアする</summary>
    public void ClearRoi()
    {
        _hasRoi = false;
        _currentRoi = Rect.Empty;
        _roiRect.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
    }

    // -------------------------------------------------------
    // マウスイベント
    // -------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_overlayCanvas);
        _dragStart = pos;

        // ハンドル判定
        if (_hasRoi)
        {
            var mode = HitTestHandles(pos);
            if (mode != DragMode.None)
            {
                _dragMode = mode;
                _roiBefore = _currentRoi;
                _overlayCanvas.CaptureMouse();
                return;
            }

            // 矩形内ドラッグ = 移動
            if (_currentRoi.Contains(pos))
            {
                _dragMode = DragMode.Move;
                _roiBefore = _currentRoi;
                _overlayCanvas.CaptureMouse();
                return;
            }
        }

        // 新規描画
        _dragMode = DragMode.DrawNew;
        _currentRoi = new Rect(pos, new Size(0, 0));
        _hasRoi = true;
        _overlayCanvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_overlayCanvas);

        // 拡大鏡の更新
        UpdateMagnifier(pos);

        // カーソル形状の更新
        if (_dragMode == DragMode.None && _hasRoi)
        {
            var hitMode = HitTestHandles(pos);
            if (hitMode != DragMode.None)
            {
                Cursor = hitMode switch
                {
                    DragMode.ResizeTL or DragMode.ResizeBR => Cursors.SizeNWSE,
                    DragMode.ResizeTR or DragMode.ResizeBL => Cursors.SizeNESW,
                    _ => Cursors.Arrow
                };
            }
            else if (_currentRoi.Contains(pos))
            {
                Cursor = Cursors.SizeAll;
            }
            else
            {
                Cursor = Cursors.Cross;
            }
        }

        if (_dragMode == DragMode.None) return;

        var delta = new Vector(pos.X - _dragStart.X, pos.Y - _dragStart.Y);

        switch (_dragMode)
        {
            case DragMode.DrawNew:
                _currentRoi = new Rect(
                    Math.Min(_dragStart.X, pos.X),
                    Math.Min(_dragStart.Y, pos.Y),
                    Math.Abs(pos.X - _dragStart.X),
                    Math.Abs(pos.Y - _dragStart.Y));
                break;

            case DragMode.Move:
                _currentRoi = new Rect(
                    _roiBefore.X + delta.X,
                    _roiBefore.Y + delta.Y,
                    _roiBefore.Width,
                    _roiBefore.Height);
                break;

            case DragMode.ResizeTL:
                _currentRoi = RectFromCorners(
                    new Point(_roiBefore.Left + delta.X, _roiBefore.Top + delta.Y),
                    _roiBefore.BottomRight);
                break;

            case DragMode.ResizeTR:
                _currentRoi = RectFromCorners(
                    new Point(_roiBefore.Right + delta.X, _roiBefore.Top + delta.Y),
                    _roiBefore.BottomLeft);
                break;

            case DragMode.ResizeBL:
                _currentRoi = RectFromCorners(
                    new Point(_roiBefore.Left + delta.X, _roiBefore.Bottom + delta.Y),
                    _roiBefore.TopRight);
                break;

            case DragMode.ResizeBR:
                _currentRoi = RectFromCorners(
                    new Point(_roiBefore.Right + delta.X, _roiBefore.Bottom + delta.Y),
                    _roiBefore.TopLeft);
                break;
        }

        // Canvas範囲内にクリップ
        ClipToCanvas(ref _currentRoi);

        UpdateRoiVisuals();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            _overlayCanvas.ReleaseMouseCapture();

            // 極小の矩形は無効とする
            if (_currentRoi.Width < 5 || _currentRoi.Height < 5)
            {
                ClearRoi();
            }
            else
            {
                RoiChanged?.Invoke(_currentRoi);
            }

            _dragMode = DragMode.None;
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _magnifier.Visibility = Visibility.Collapsed;
    }

    // -------------------------------------------------------
    // ビジュアル更新
    // -------------------------------------------------------

    private void UpdateRoiVisuals()
    {
        if (!_hasRoi || _currentRoi.Width <= 0 || _currentRoi.Height <= 0)
        {
            _roiRect.Visibility = Visibility.Collapsed;
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
            return;
        }

        _roiRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(_roiRect, _currentRoi.X);
        Canvas.SetTop(_roiRect, _currentRoi.Y);
        _roiRect.Width = _currentRoi.Width;
        _roiRect.Height = _currentRoi.Height;

        // ハンドル位置
        PositionHandle(_handles[0], _currentRoi.Left, _currentRoi.Top);     // TL
        PositionHandle(_handles[1], _currentRoi.Right, _currentRoi.Top);    // TR
        PositionHandle(_handles[2], _currentRoi.Left, _currentRoi.Bottom);  // BL
        PositionHandle(_handles[3], _currentRoi.Right, _currentRoi.Bottom); // BR

        foreach (var h in _handles) h.Visibility = Visibility.Visible;

        UpdateRoiColor();
    }

    private void UpdateRoiColor()
    {
        if (!_hasRoi) return;

        bool matched = MatchScore >= Threshold;
        var color = matched
            ? Color.FromRgb(76, 175, 80)   // 緑 (#4CAF50)
            : Color.FromRgb(255, 69, 58);  // 赤

        _roiRect.Stroke = new SolidColorBrush(color);
        _roiRect.Fill = new SolidColorBrush(Color.FromArgb(matched ? (byte)40 : (byte)20, color.R, color.G, color.B));
    }

    private static void PositionHandle(Rectangle handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - HandleSize / 2);
        Canvas.SetTop(handle, y - HandleSize / 2);
    }

    // -------------------------------------------------------
    // 拡大鏡
    // -------------------------------------------------------

    private void UpdateMagnifier(Point mousePos)
    {
        if (!ShowMagnifier || SourceImage == null)
        {
            _magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        _magnifier.Visibility = Visibility.Visible;

        // 拡大鏡の表示位置（マウスの右上に配置、画面端では左に反転）
        double magX = mousePos.X + 20;
        double magY = mousePos.Y - MagnifierSize - 10;

        if (magX + MagnifierSize > _overlayCanvas.ActualWidth)
            magX = mousePos.X - MagnifierSize - 20;
        if (magY < 0)
            magY = mousePos.Y + 20;

        Canvas.SetLeft(_magnifier, magX);
        Canvas.SetTop(_magnifier, magY);

        // BitmapSourceから拡大領域を切り出し
        try
        {
            var bmp = SourceImage;
            var canvasW = _overlayCanvas.ActualWidth;
            var canvasH = _overlayCanvas.ActualHeight;

            GetImageRenderRect(canvasW, canvasH, bmp.PixelWidth, bmp.PixelHeight,
                out double renderX, out double renderY, out double renderW, out double renderH);

            if (renderW <= 0 || renderH <= 0) return;

            // マウス位置をピクセル座標に変換
            double px = (mousePos.X - renderX) / renderW * bmp.PixelWidth;
            double py = (mousePos.Y - renderY) / renderH * bmp.PixelHeight;

            // 拡大鏡に表示するピクセル範囲
            double halfViewPx = MagnifierSize / MagnifierZoom / 2.0 * bmp.PixelWidth / renderW;

            int srcX = (int)Math.Max(0, px - halfViewPx);
            int srcY = (int)Math.Max(0, py - halfViewPx);
            int srcW = (int)Math.Min(halfViewPx * 2, bmp.PixelWidth - srcX);
            int srcH = (int)Math.Min(halfViewPx * 2, bmp.PixelHeight - srcY);

            if (srcW > 0 && srcH > 0)
            {
                var cropped = new CroppedBitmap(bmp, new Int32Rect(srcX, srcY, srcW, srcH));
                _magnifierImage.Source = cropped;
            }
        }
        catch
        {
            // 安全に無視
        }
    }

    // -------------------------------------------------------
    // ヒットテスト
    // -------------------------------------------------------

    private DragMode HitTestHandles(Point pos)
    {
        double tolerance = HandleSize + 4;

        if (Distance(pos, _currentRoi.TopLeft) < tolerance) return DragMode.ResizeTL;
        if (Distance(pos, _currentRoi.TopRight) < tolerance) return DragMode.ResizeTR;
        if (Distance(pos, _currentRoi.BottomLeft) < tolerance) return DragMode.ResizeBL;
        if (Distance(pos, _currentRoi.BottomRight) < tolerance) return DragMode.ResizeBR;

        return DragMode.None;
    }

    // -------------------------------------------------------
    // ユーティリティ
    // -------------------------------------------------------

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Rect RectFromCorners(Point a, Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void ClipToCanvas(ref Rect rect)
    {
        double maxW = _overlayCanvas.ActualWidth;
        double maxH = _overlayCanvas.ActualHeight;
        if (maxW <= 0 || maxH <= 0) return;

        if (rect.X < 0) rect = new Rect(0, rect.Y, rect.Width + rect.X, rect.Height);
        if (rect.Y < 0) rect = new Rect(rect.X, 0, rect.Width, rect.Height + rect.Y);
        if (rect.Right > maxW) rect = new Rect(rect.X, rect.Y, maxW - rect.X, rect.Height);
        if (rect.Bottom > maxH) rect = new Rect(rect.X, rect.Y, rect.Width, maxH - rect.Y);
    }

    /// <summary>
    /// Stretch=Uniformの場合の画像レンダリング位置・サイズを計算する。
    /// </summary>
    private static void GetImageRenderRect(
        double canvasW, double canvasH, double imgW, double imgH,
        out double renderX, out double renderY, out double renderW, out double renderH)
    {
        double scaleX = canvasW / imgW;
        double scaleY = canvasH / imgH;
        double scale = Math.Min(scaleX, scaleY);

        renderW = imgW * scale;
        renderH = imgH * scale;
        renderX = (canvasW - renderW) / 2.0;
        renderY = (canvasH - renderH) / 2.0;
    }
}
