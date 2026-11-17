# 軽量化MODとMoreUseResourcesを併用する設計

対象はWindows版RimWorld 1.7.5011 rev24、.NET 10、Waylith、および[PR #5の調査基準 `94ed10a`](https://github.com/pengin0503/Rimworld-MoreUseResources/pull/5/commits/94ed10aebe842ae396a1c976e8bf1ddccb0f9210)。WOWGAG、Axen、Overclockが短縮するTick・GUI処理を活かし、MoreUseResourcesは空きCPU・RAM・VRAMをアセットの先読み・検査とゲーム状態の解析・表示に使う。想定環境はRyzen 7 9700X（16論理CPU）、RX 7800 XT（VRAM 16 GB）、RAM 32 GB、約270〜280 MOD。3 MODは個別に任意で導入できる形とする。

## 確認した経路と担当

提供されたWOWGAGとOverclockの1.7用DLL、Axen v1.1.1のDLLと[同リリースに対応する公開ソース](https://github.com/Axen-o7/Axen-s-Performance-Enhancements/tree/fa2d07054cdd630ba14336180310ad12aa6fc9a3)、およびMoreUseResourcesのコードを静的に照合した。Axen作者の確認ビルドは1.7.4990 rev1069。実際に適用されたパッチは、起動完了後に利用者が診断から確認できる設計とする。

| 処理 | 確認した重複 | 併用時の扱い |
| --- | --- | --- |
| JIT | WOWGAGに `jitProfileReplay`、`precompilePatched`、`afterLoadPrecompile` がある。MoreUseResourcesはWOWGAGが使用中、または状態を判定できない場合に自分のJITを待機させる | 既存の優先判定を維持する |
| 起動時のXML | WOWGAGの `reusePatchResult` がXMLの読み込みとパッチ適用を扱う。MoreUseResourcesの計測フックは `LoadModXML` と `CombineIntoUnifiedXML` にも入り、WOWGAGのXML読み込み省略判定に影響し得る | 起動時にWOWGAGが有効又は判定不能なら、この2工程の計測とMoreUseResources自身のXMLキャッシュフックを譲る。残る工程と起動全体は計測する |
| 予約・ガス・ワールド更新 | AxenとOverclockは予約とガスに重複がある。WOWGAGもガス更新に触れ、確認した版には条件付きでAxenの `World dispatch` を解除する経路がある | 実際に登録されたHarmonyオーナーを診断に出す。競合の判断は3 MODが持つ既存の優先処理と利用者の設定に委ねる |
| 並列処理 | WOWGAGはAxenなどを検出して並列機能を止める経路がある。OverclockはWOWGAGの特定の並列設定を見て自分の並列Pawn Tickを見送る | 外部MODの設定と実際に有効な状態を区別し、MoreUseResourcesのCPU枠だけを調整する |
| 実アセット | MoreUseResourcesは読み取り専用のパック先読み、OSキャッシュ、アセット検査、Waylithの実テクスチャ先読みを持つ | キャッシュに載せる対象とタイミングを改善し、実際に使うデータへ容量を配分する |

## 起動互換と診断

- `wowgag.guiperformancepatch` をパッケージIDで識別し、当該MOD内の `GUIPerformancePatch.GppMod.Settings.reusePatchResult` が既知の `bool` の場合だけ値を読む。有効・無効・判定不能・未導入を区別し、起動時に固定する。設定インスタンス未準備、型変更、例外も判定不能として扱う。
- 対象の2工程を譲った場合は「WOWGAG優先で計測省略」と診断に出す。ゼロ秒として記録すると読み込みが速かった計測と混同するため、工程の計測件数にも含めない。WOWGAGが無効／未導入の場合に既存設定・既存の外部パッチ判定を使う。
- ロード完了後とホットリロード完了後に、既存のHarmony APIで対象メソッドの登録オーナーを絞って取得する。対象は予約の2メソッド、`GasGrid.Tick`、`WorldObjectsHolder.WorldObjectsHolderTick`、`PathFinder.ScheduleGridJobs`、およびWOWGAGと競合するXML工程。メソッド単位の表示は「登録あり」の意味で扱う。起動ログは1回と、診断内容が変化したときだけ出す。
- Overclockの実稼働判定には、対象DLLにある `Overclock.Optimisations.ParallelPawnTicks.active` の静的 `bool` を使う。WOWGAGは設定と当該機能の装着・停止条件を照合し、確認できた場合だけ有効/無効と判定する。`GppParallel.Active` は一時的なジョブ深度依存のため、ゲーム中の定期ポーリングから無効を推定する用途に使わない。Axenの経路探索はHarmonyオーナーの登録を診断に出し、プレイ中の負荷は既存フレーム/TPS制御に反映させる。
- 外部DLLの読み取りはゲームスレッドで行い、既定のJIT互換確認と同じ2秒程度の間隔で、純粋な状態値だけをコントローラーへ渡す。外部MODの欠落・未知の版・反射失敗を許容する。DLLの追加依存と外部MODの設定・パッチへの書き込みは範囲外とする。

## CPU・RAM・VRAMの使い方

### CPUワーカー

- 通常・高速のプレイ中、WOWGAGまたはOverclockの並列処理が有効、または該当MODが導入済みで状態判定不能なら、MoreUseResourcesのワーカーを最大4に抑える。4は16論理CPU機での比較開始値。利用者の設定、フェーズ倍率、適応制御、フレームガードがさらに少ない値を求める場合はその値を使う。起動・ロード・タイトル・ポーズでは既存制御を使う。
- コントローラーは低優先度のワーカープールを再利用する。上限は新規の仕事の取得に適用し、実行中のI/OやJITは既存の安全な区切りまで進める。セーブ、フォーカス、空きRAM、手動停止の各条件を引き続き優先する。
- 有効な上限、判定元、外部MODの有効／無効／判定不能を設定画面で確認できるようにする。Playの状態・選択速度・実効速度が変わる際の計測窓の切替は既存コードを再利用する。

### RAM

- `RamHoldCopies` のネイティブコピー予算が足りない同じファイルは、既存の読み取り許可が残っている場合にOSファイルキャッシュだけを温める。コピーは既存の再試行条件で再申請する。OS先読み成功・コピー保留を別々に数える。
- OSの空きRAM不足、停止、セーブ制御、ファイルソース世代の失効も既存判定に従う。パックの読み取り扱い・コピーの鮮度チェック・再先読み間隔は既存経路を使う。

### VRAM

- 需要優先が有効なとき、Waylithで実際に再読み込みされたテクスチャの順位を最大512件保存し、次回起動の先読み候補の先頭に使う。MoreUseResources自身による再読み込みは既存の差し引きを再利用する。セーブには組み込まず、ユーザーデータ内の任意のキャッシュとして扱う。
- 保存キーはパッケージIDとコンテンツホルダー内の相対キーを組にする。名前衝突や置き換え防止のため、順序付きMOD構成・ゲームビルド・Waylithキャッシュ方式とパックの更新情報の指紋を比較し、現セッションで解決できるテクスチャだけを候補とする。正式なパックの特定ができない場合はセッション内の優先処理を使う。ファイルの絶対パスを履歴に書かない。
- 入力は512件・ファイル1 MiB上限、失敗時は通常走査。生データの読書きはワーカーに任せ、ゲームオブジェクトの解決とアップロードはメインスレッドで分割する。既存のフレーム予算、GPUドライバー予算、空きVRAM、アトラス除外を両方の候補に適用する。
- 追加VRAM確保が可能かはドライバー予算で決まり、既定の最低空き2 GiBと最大75%の制限を使う。必要なテクスチャが揃えばアップロード作業は終了する。

## ゲーム状態を使う追加処理と完了判定

- [マップ・物資・セーブを使う追加計画](../plans/2026-11-17-game-state-resource-work-ja.md)に、CPUでの集計、RAMでの一時保持、生成データのGPU描画、Waylithのネイティブ計算、セーブ先読みを記録した。技術的に実装可能でゲーム内の使い道が見込める機能は、速度向上や有用性が未計測でも実装対象とする。
- 今回の完了判定は単体テスト、静的API照合、Releaseビルド、同梱DLLと配布物の確認までとする。実ゲームの起動、プレイ、性能測定は別工程とする。
- [Windows実機測定ガイド](../../testing/windows-waylith-benchmark-ja.md)には、3 MODだけ、現行MoreUseResources込み、新しい設定を別途比較するときの条件を残す。TPSやフレーム時間の閾値は設定の実測比較に使う参考値とする。
- 先読みと検査のI/O統合、パックの範囲単位先読みは既存の追加候補。読み取り範囲を絞る案には、WindowsのファイルI/Oとハードフォールトのトレースを使う。

[実装順と受入条件](../plans/2026-11-17-performance-mod-resource-coexistence-ja.md)
