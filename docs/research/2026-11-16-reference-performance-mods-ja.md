# 3つのパフォーマンスMODからの技術調査と互換方針（2026-11-16）

## 調査の前提

ユーザー提供のZIPを確認した。対象は [WG] WOWGAG's Performance Patch、Overclock、
Axen's Performance Enhancements。これは添付時点のバイナリ・付随XMLに関する調査であり、
特定版の全Harmonyパッチ一覧や実機互換性を証明するものではない。

- WOWGAG：1.7用 `GUIPerformancePatch.dll`、`Analyzer.xml`、`About.xml`、
  日英などの翻訳を含む。GUI描画・テキスト計測・tick処理のキャッシュや頻度制御を提供する。
  付属のアナライザー一覧には `GppStatCache`、`GppPawnCompTick`、
  `GppWorldObjTick`、`GppGasRate` などが存在する。
- Overclock：1.7用 `Overclock.dll` と各種 `Audit/*.txt` を含む。
  予約処理、Beauty、Storage、Pawn・World Tickなど多数の経路を最適化する。
  Pawnの並列tickは実験機能であり、順序や乱数結果の違いを起こし得る。
  `About.xml` は RimThreaded系との非互換を宣言している。
- Axen：`AxensPerformanceEnhancements.dll`、`About.xml`。
  1.7.4990 rev1069が最終テスト版と記され、予約の索引化、パス出力、
  ガス、ワールドdispatch、部屋処理などに集中している。
  公開ソースのライセンスはGPL-3.0。DLLの直接流用・同梱は行わない。

## MoreUseResourcesとの棲み分け

本MODの目的は余剰CPU・RAM・GPUの**実データ処理への利用**であり、各種最適化MODの
tickループやGUI描画を置き換えることではない。最適化MODは処理量を減らすため、
その実装を安易に併合してもCPU/GPU利用率の増加につながるとは限らない。

| 対象 | 採用する方向性 | 回避すること |
| --- | --- | --- |
| WOWGAG | キャッシュ・索引化と状態表示の構想を参考に、実アセットの読み取り・指紋索引を独立させる | GUI・Tick・Statへの重複Harmonyパッチ |
| Overclock | 並列処理の副作用を踏まえ、ゲームオブジェクトではなく不変に近いディスクファイルを低優先度で処理する | Pawn Tick並列化、乱数・ゲーム状態を別スレッドで更新 |
| Axen | 小さな実作業を個別に有効化する構成を参考に、CPU索引機能を独立トグルにする | 経路探索、予約、World dispatchへの重複パッチ |

## 今回実装した機能

`AssetAudit` は有効MODのアセットを読み取り専用でSHA-256索引化し、
完全一致の重複ファイル数・容量・検査済みバイト数・エラー数を表示する。
既存の `GameAssetFiles.Discover` が列挙したファイルを使い、
CPUワーカーで実行する。RAM先読みを無効にしてもCPU索引のみ動作可能。

安全制約：フェーズ単位の停止・保存中制御・メモリ余裕・
フレームガードと低優先度を維持し、1ファイル上限1GiB、
対象列挙上限5万件、同時ジョブ数上限32を守る。
ファイル書込・ゲーム状態変更・セーブ内容変更はしない。

## 限界と要検証事項

1. 索引化の結果は診断用であり、ゲーム内部のXMLロードやテクスチャ読込に直接渡さない。
   したがってFPS/TPS・ロード時間の改善は主張しない。
2. ZIPのDLLに対する完全なHarmony競合表、Windows実機のGPU・RAM使用量、
   上記3つのMODと併用した状態のTPS比較は未検証。
3. OverclockのPawn並列化を有効にして実験する場合は、
   このMODよりもゲーム側のスレッド安全性・MOD互換性の検証が優先される。
4. WOWGAGとOverclockとAxenのコード・DLLは取り込まない。
   公開ソースの複製・派生が必要な場合は別途ライセンスを満たす。
