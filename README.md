# Chohan (丁半) - プロジェクト構成

## ディレクトリ構成

```
Chohan/
├── Chohan.sln
├── README.md
│
├── Chohan.Core/                    # ビジネスロジック層 (クラスライブラリ)
│   ├── Chohan.Core.csproj
│   ├── Capture/
│   │   └── CaptureEngine.cs        # DirectShow経由のカメラキャプチャ
│   ├── Config/
│   │   ├── AppConfig.cs             # 共通設定モデル (config.json)
│   │   ├── GameProfile.cs           # ゲーム別プロフィール (Profiles/*.json)
│   │   ├── ConfigService.cs         # 設定永続化サービス
│   │   └── DpapiHelper.cs           # DPAPI暗号化ヘルパー
│   ├── Recognition/
│   │   ├── TemplateMatchingEngine.cs
│   │   ├── RoiSettings.cs
│   │   └── TriggerConfig.cs
│   ├── State/
│   │   ├── GameState.cs
│   │   └── StateMachine.cs
│   └── Twitch/
│       ├── TwitchOAuthConfig.cs
│       ├── TwitchOAuthService.cs
│       ├── TwitchPredictionService.cs
│       └── TwitchTokenStore.cs
│
├── Chohan.App/                     # WPFアプリケーション層
│   ├── Chohan.App.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── Controls/
│   │   └── RoiEditorControl.cs
│   ├── Views/
│   │   ├── MainWindow.xaml / .cs
│   │   ├── SettingsWindow.xaml / .cs
│   │   └── TwitchSettingsWindow.xaml / .cs
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs
│   │   ├── MainViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   └── TwitchSettingsViewModel.cs
│   ├── Converters/
│   └── Resources/Styles.xaml

実行時ファイル構成（ポータブル形式）:
  Chohan.exe
  config.json                   ← 共通設定・暗号化トークン
  Profiles/
    Default.json                ← ゲーム別トリガー設定
    StreetFighter6.json
  Templates/
    default_start.png           ← ROI切り抜きテンプレート画像
```

## アーキテクチャ

- **MVVM パターン**: View ↔ ViewModel ↔ Core(Model)
- **Core層**: UI非依存。カメラキャプチャ、画像認識、状態管理を担う
- **App層**: WPFのUI。CoreをDIまたは直接参照して利用

## 依存パッケージ

| パッケージ | 用途 |
|---|---|
| OpenCvSharp4 | 画像処理・テンプレートマッチング |
| OpenCvSharp4.runtime.win | Windows用ネイティブバインディング |
| OpenCvSharp4.Extensions | BitmapSource変換等 |
| DirectShowLib | OBS仮想カメラからのキャプチャ |
