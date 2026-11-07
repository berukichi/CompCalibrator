# 機器プロファイルの調査状況

2026-11-06 時点。プロファイルの本体は `src/CompCalibrator.Core/Profiles/Data/*.json`。

## 確かさの区分

| タグ | 意味 |
| --- | --- |
| VERIFIED | 公開された資料やソースコードで確認した |
| APP | 実機のアプリの表示で確認した |
| INFERRED | 画面の位置や他製品からの推定 |
| UNVERIFIED | 可動域を確認できていない仮置き |

## 機器ごとの状況

| 機器 | 状況 | 根拠 |
| --- | --- | --- |
| BRIDGE CAST | LEGACY MODE の 5 項目はほぼ確定。残りは RELEASE の 3 段と NOISE SUPPRESSOR LEVEL の範囲 | 実機の画面、アプリの表示テーブル |
| GoXLR / Mini | 全項目 VERIFIED | GoXLR Utility のソース(メーカー公表値ではない) |
| Vocaster | 固定値は VERIFIED。Compress の可動域は未公表 | Vocaster Hub User Guide V1.3 |
| OBS Studio | 全項目 VERIFIED | compressor-filter.c |
| AG03MK2 / AG06MK2 / AG03 / AG06 / AG01 | 項目名と RATIO の両端(1.1〜INF)を確認。他の可動域は UNVERIFIED | Yamaha FAQ(数値の記載なし) |
| RØDECaster Pro II / Duo | 構成(VoxLab と Advanced)を確認。比率の上限 4.5:1 以外の可動域は UNVERIFIED | 公式ガイド(数値の記載なし)、Sound On Sound |
| Wave Link | 項目のみ確認。可動域は UNVERIFIED | Elgato の説明と Wave XLR のガイド(コンプの記載なし) |
| Revelator io24 / io44 | 項目、Auto の 10ms / 150ms、HPF 24Hz〜1kHz を確認。Standard の可動域は UNVERIFIED | io44 と io24 のマニュアル(数値の記載なし) |

## BRIDGE CAST の確認方法

アプリ内の表示テーブルと、Ver.5.01 Build 722 の実機の画面を突き合わせた。
`tests/CompCalibrator.Tests/EngineTests.cs` の `BridgeCastMatchesTheSliderPositionsOnScreen` が、下の位置をすべて再現することを確かめている。

| 項目 | 左端 | 右端 | 中間の確認 |
| --- | --- | --- | --- |
| THRESHOLD | -48dB | 0dB(算出) | -30dB = 37.5%、-21dB = 56.3% |
| RATIO | 1.00:1 | Inf:1 | 2.00:1 = 46.2%、2.50:1 = 53.8%、3.20:1 = 61.5% |
| ATTACK | 0.0ms | 100ms | 20ms = 20%、50ms = 50% |
| RELEASE | 50ms | 5000ms | 130ms = 20%、200ms = 30%、640ms = 55% |
| POST GAIN | +0dB | +30dB | +17dB = 56.7% |
| LOW CUT | Flat | 500Hz | 31.5Hz = 20% |
| NOISE SUPPRESSOR LEVEL | 未確認 | 未確認 | -24dB = 60% |

POST GAIN の上限は、当初 +18dB と推定していたが、実機では +30dB だった。

### 実機で確かめたい値

- RELEASE の 2〜4 段目(60 / 80 / 100ms の推定)
- NOISE SUPPRESSOR LEVEL の両端(-60〜0dB の推定)

LEGACY MODE OFF の操作は `PEAK REDUCTION` と `GAIN` の 2 つ。目盛りと動作の対応が不明なので、数値ではなく GR メーターの目標を示している。

## 公式資料に数値がない機種

Yamaha の FAQ、RØDE のユーザーガイド、PreSonus のマニュアル、Wave XLR のクイックスタートガイドを読んだが、コンプレッサーの可動域の数値は載っていなかった。
これらの機種は、BRIDGE CAST と同じく実機の画面の両端と中間の値で確定する。

Revelator の FET モデルは比率が 4:1 / 8:1 / 12:1 / 20:1 / All の固定で、スレッショルドの代わりに Input Gain を使う。スレッショルド方式の計算に合わないのでプロファイルに入れていない。

## 未対応

TASCAM MiNiSTUDIO / SERIES 102i、Steinberg UR-C、Shure MOTIV、Beacn はプロファイルなし。
数値を確認できなかったため、推測の値を入れずに見送った。

## プロファイルの追加と修正

アプリの「プロファイルのフォルダーを開く」から開くフォルダーに JSON を置くと読み込む。同梱と同じ `id` なら置き換える。
場所は、パッケージなしなら `%LOCALAPPDATA%\CompCalibrator\Profiles`、MSIX ならパッケージの LocalState の下。
