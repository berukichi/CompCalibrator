# Comp Calibrator

マイクの声を測って、配信で使うオーディオインターフェースのコンプレッサー設定を計算する Windows アプリ(WinUI 3)。
結果は機器の画面と同じ項目、同じ刻みの値で出す。

## 使い方

1. 機器、コンプレッサーの種類、マイク入力、スタイルを選ぶ
2. 機器のコンプレッサーを OFF にして、画面の指示に沿って 22 秒話す(無音 → 普段の声 → 大きな声 → 小さな声)
3. 推奨設定を機器に入力する
4. コンプレッサーを ON にして測り直し、予測との差で補正する

## 測るもの

| 分類 | 項目 |
| --- | --- |
| 声 | 基本周波数(YIN)、フォルマント F1〜F3(線形予測)、息の成分 |
| 音色 | スペクトル重心、高域の傾き、低域、プレゼンス、歯擦音 |
| 動き | 話す速さ、音節の立ち上がり |
| レベル | ラウドネス(BS.1770)、音量の幅、クレスト、ノイズ床、SN 比 |

## 計算の考え方

アタックとリリースは声から求める。

- アタック: 基本周期の 2 倍を下限にする。これより短いと波形そのものを追って低音が歪む
- リリース: 音節の周期に合わせる。基本周期の 10 倍を下限にする。ノイズが多いときと歯擦音が強いときは長くする

スレッショルドと比率は、録音を機器の刻みの全候補でシミュレーションして選ぶ。音量の幅、普段の GR、山の GR、ゲインの揺れ、比率の高さを合わせて評価する。
メイクアップは -18 LUFS を目標にし、ピークが -1 dBFS を超えない範囲にとどめる。

機器のスレッショルドの基準が PC の dBFS と同じとは限らないので、最後は実機の測定で補正する。

機器の可動域の調査状況は [docs/device-profiles.md](docs/device-profiles.md) を参照。

## ビルド

```
dotnet test tests/CompCalibrator.Tests/CompCalibrator.Tests.csproj
dotnet build src/CompCalibrator.App/CompCalibrator.App.csproj -p:Platform=x64
```

パスが長い場所では、XAML コンパイラのビルドが失敗し(WMC9999)、実行ファイルも Windows App SDK の DLL を読み込めずに起動できない(0x800700CE)。短いパスに置いてビルドと実行を行う。

開発用に `CompCalibrator.exe --analyze <wav>` で録音ファイルを解析できる。

## MSIX

```
powershell -ExecutionPolicy Bypass -File scripts\build-msix.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-msix.ps1 -Install
```

開発用証明書(CN=beruk)で署名する。`-Install` を付けると入れ直して起動する。出力先は `dist`。
マニフェストの `microphone` の宣言により、Windows のプライバシー設定にマイクを使うアプリとして表示される。

Microsoft Store に出すときは `Package.appxmanifest` の `Identity` を Partner Center の値に置き換える。

アイコンは `scripts\make-icons.ps1` で作り直せる。

## ライセンス

[MIT License](LICENSE) で公開している。使用しているライブラリの表記は [THIRD-PARTY-NOTICES.txt](src/CompCalibrator.App/THIRD-PARTY-NOTICES.txt) を参照。

このアプリは各メーカーとは無関係の個人制作物で、メーカーの承認や保証を受けたものではない。製品名は各社の商標または登録商標で、対応機器を示すためにのみ使用している。MIT License は第三者の商標を使う権利を与えるものではない。

## 構成

| パス | 内容 |
| --- | --- |
| `src/CompCalibrator.Core` | 解析、シミュレーション、計算、プロファイル |
| `src/CompCalibrator.App` | WinUI 3 の画面、録音、試聴 |
| `tests/CompCalibrator.Tests` | 合成音声によるテスト |

デザインは berukichi の index(墨色の方眼、等幅、ライムのアクセント)を移植した。JetBrains Mono と Inter Tight は同梱しておらず、入っていない環境では Cascadia Mono と Segoe UI Variable で表示する。
