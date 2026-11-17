# 最適化MODにリソースを優先配分する実装計画

> 実装担当は `superpowers:executing-plans` または `superpowers:subagent-driven-development` で、各作業のテスト結果を確認してから次へ進む。

> **実装時の変更（2026-11-17、依頼者の指示）**: 本計画のうち「外部MOD稼働時は最大4ワーカー」「Axenの共有プール混雑時は最大1ワーカー」とその復帰処理は実装しない。外部MODの稼働や共有プールの混雑を理由に、MoreUseResourcesのワーカー上限と処理量を下げない。同じ作業単位だけを譲り、空いた枠は別の独立したバニラ処理に回す。結果は [静的検証の記録](../../review/2026-11-17-external-mod-coexistence-ja.md) を参照。

**目標**
WOWGAG、Axen、Overclockが担当するバニラ処理をそれぞれに実行させ、増枠できる処理は本来のワーカー設定で増枠する。MoreUseResourcesは競合時に自分の仕事を譲り、余った枠で別の既存処理を準備する。

**対象**
RimWorld 1.7.5011 rev24、Windows 11、.NET 10、Waylith。PR #5の既存ブランチで進める。併用の基準は [併用設計](../specs/2026-11-17-performance-mod-resource-coexistence-ja.md)、手順は [既存の併用計画](2026-11-17-performance-mod-resource-coexistence-ja.md) と [バニラ処理への追加活用](2026-11-17-game-state-resource-work-ja.md)。従来の固定上限案は本計画の判定に組み込む。

## 静的調査で確認した割り当て先

| 処理の担当 | 設定・実経路 | 今回実装する割り当て |
| --- | --- | --- |
| WOWGAG | `GppSettings.parWorkers` は整数。提供ZIPの1.7版では設定UIの範囲は0〜32。0は物理コア数−1を基準にゲームのジョブ上限も参照する自動設定。登録済みフックと設定値から、使用可能か別に確認する | 稼働する並列機能に限り、明示指定で既存のワーカー設定を使う。XML再利用・JIT・世界地図やセーブの事前準備はWOWGAGを優先する |
| Overclock | `OverclockSettings.parallelThreads` は整数。0は自動、設定UIの上限は提供版で論理CPU数−1。表示文言は再起動後に反映と明記。並列Pawn TickがWOWGAGの特定の並列設定で見送られる分岐もある | 明示指定で既存のワーカー数を変更できるようにする。増枠はOverclock側で並列Pawn Tickが有効になる構成に効く。並列化トグルとWOWGAGとの優先判定は各MODが管理する |
| Axen | `axeno7.performanceplus.path-dag` が独立した経路探索グリッドのバニラジョブを並列発行する。Unity互換側のスケジューラーはWaylithの共有ジョブランタイムへ渡す。提供版にAxen専用の枠を設定する入口は確認できなかった | Axenのパッチが入った状態で共有プールに仕事が残っていたら、MoreUseResourcesの追加CPU作業を抑えて空きを渡す。共有プールの観測値はAxenの実測値として表示しない |

根拠はユーザー提供3 MODの1.7 DLL・同梱設定文言、[AxenのPathDag](https://github.com/Axen-o7/Axen-s-Performance-Enhancements/blob/fa2d07054cdd630ba14336180310ad12aa6fc9a3/Source/AxensPerformanceEnhancements/Pathfinding/PathDag.cs)、[1.7.4985のWaylithジョブ実装](https://github.com/codexvn/RimWorldDecompiled/blob/d5cbe1186bbb9962b7bd4913c900441c6fe4a4ff/UnityStubs/Unity.Jobs/UnityJobScheduling.cs)。1.7.5011の参照DLLで共有ランタイムの `HasPendingWork` と `WorkerCount` の取得口を確認済み。配布版のMOD設定ファイルの形式は、適用前に照合する。

ワーカー設定が効くのは並列化された既存処理。キャッシュによって省略された走査や、直列で走る予約・ガス・GUI処理に、追加のスレッドを足しても並列化されない。RAM・VRAMは共有資源なので、他MODが既に作ったキャッシュやメッシュを優先し、MoreUseResources側の先読み・常駐・アップロードを予算で後退させる。

## 1　実際の動作状況を読み取る

**対象** `Source/Game/WowgagCompatibility.cs`、併用計画の `PerformanceModCompatibility.cs`、`ResourceRuntime.cs`。ゲームスレッドで確認し、Coreにゲーム型を渡さない。

- [ ] 併用計画の有効・無効・判定不能・未導入の区別を実装する。WOWGAGの `parWorkers`、Overclockの `parallelThreads` は設定値として表示し、0は自動と表示する。認識できる実稼働値がある場合は別項目にし、未確認値を推定で埋めない。
- [ ] WOWGAGの並列ブロックとOverclockの `ParallelPawnTicks.active` を確認する。設定ONでも停止された場合を区別する。Axenは対象の `PathFinder.ScheduleGridJobs` と実際のHarmonyオーナーを確認する。
- [ ] Axenの対象パッチ確認後、ロード完了済みの安定したフェーズで、対象版の `WaylithJobRuntime.HasPendingWork` を共有プールの混雑シグナルとして読む。起動中の参照でエンジンを先に初期化しない。1.7.5011で取得できない版では既存のフレーム/TPS制御を用いる。
- [ ] 単体テストで、ロード順の違い、判定不能、設定ONでもブロック中、共有プールが忙しい／空いた、実行中のMOD設定切替を確認する。

## 2　MoreUseResources側の枠を後退・回復させる

**対象** `Source/Core/ResourceController.cs`、`AdaptiveWorkerBudget.cs`、`WorkerPool.cs`、互換方針テスト。既存のフェーズとワーカー制御を使用する。

- [ ] 同じメソッドや出力キャッシュを他MODが担当する場合、その作業単位をMoreUseResourcesのキューから外す。無関係なファイル、機能、フェーズの作業は従来の条件で継続する。未知版は重なる可能性がある処理だけを停止する。
- [ ] 通常・高速プレイ中の外部並列機能が有効または判定不能なら、既存の4ワーカー上限案を暫定値として適用する。Axen対象パッチが登録され、共有プールに仕事が残っている間は追加作業をさらに最大1ワーカーへ抑える。設定・フェーズ・既存の適応制御がより少ない値を要求するときはそちらを優先する。
- [ ] 圧力がなくなったら既存の段階的な回復を使い、各ワーカーが次の作業を取る時点で反映する。停止・セーブ・フレームガードを守る。タイトルやポーズ中も、外部MODの起動処理やメッシュ準備が同じ資源を使用中なら譲る。論理CPU数の固定割り当て、プロセス全体の優先度変更、外部MODのスレッド停止は使わない。
- [ ] テストで、重複対象だけ除外、外部稼働時の上限、混雑時の1枠、上限・省電力設定による0枠、圧力解消後の段階的な回復、セーブ時停止を確認する。独自のCPU負荷やVRAM予約で枠を使い切らない。

## 3　既存のワーカー設定を明示指定で増やせるようにする

**対象** 既存のWindowsランチャー `tools/Start-RimWorldGc.ps1`、必要な範囲のPowerShellテストとREADME。

- [ ] 通常起動を維持し、利用者がMODの設定画面で増枠する手順をREADMEとMoreUseResourcesの状態欄に記載する。
- [ ] ランチャーに、既知のWOWGAGかOverclockの既存設定を明示指定で適用するオプションを追加する。候補インターフェースは `-ExternalWorkerOwner Wowgag|Overclock`、`-ExternalWorkerCount <int>`、`-ExternalSettingsPath <path>`。3引数を指定した場合だけ適用する。設定ファイルが永続的に変わり、Overclockは次回起動から反映されることを表示する。
- [ ] RimWorldの `SettingsBlock/ModSettings` 構造と対象の提供版が読み込む設定名・型を確認し、明示指定されたファイルを検証する。MOD名・設定型との一致が確認できなければ設定画面を案内する。新しい設定ファイルの推測作成や全設定の再生成は避ける。
- [ ] 範囲内のワーカー数フィールドだけを変更する。元ファイルをバックアップし、同じフォルダの一時ファイルを原子的に置換する。ファイルが直前に変更された、ゲームが既に起動中、置換失敗の場合は元の設定を保持する。重複・優先判定や並列化のON/OFFは書き換えない。Axenは設定案内と枠の譲渡で完了する。
- [ ] 一時ファイルで、未指定の場合の無変更、0と上限、範囲外、対象違い、未知XML、既存値同一、バックアップ、競合・置換失敗を確認する。PowerShellの検証時にゲームを起動しない。

## 4　担当処理の結果を再利用し、バニラの別工程に残りを使う

- [ ] 既存のバニラ計画を進める。JIT・XMLはWOWGAG優先、Axenの経路グリッドとOverclockの並列Pawn Tickは担当MODに任せる。WOWGAGが世界地図の事前生成や同じセーブの先読みをしている場合は、生成済みの結果や既存OSキャッシュを使い、同じものを再準備しない。
- [ ] 追加候補のうち、**通常描画用メッシュのGPU転送**を先に調べる。Waylithが再利用する生成済みメッシュを対象に、ゲーム本体・他MODの転送待ち／転送済みを判別できる経路で、フレーム予算・ドライバー予算内のものだけ準備する。CPUデータの可読性を保持する。
- [ ] **通常のシェーダーキャッシュの先読み**は、対象版の本体が実際に読む `.spv-cache` に絞り、通常のファイルキャッシュへ載せる。起動時に共有コンパイラーを再構成できる仕様と所有者が確認できるまで、稼働中のコンパイル枠変更は行わない。
- [ ] **既存の区画メッシュ更新・ポートレート準備**は、WOWGAGや他の描画MODが同じ工程を担当していない場合に限り、既存のdirty判定・キャッシュキー・ライフサイクルを利用する。描画のためのライブゲームオブジェクトをバックグラウンドに渡さない。
- [ ] 新しいマップ画面、集計、予測、描画効果は追加せず、元からロード・描画・Tickが消費する結果を対象にする。新旧ゲーム版の静的確認で結果の再利用経路が成立するものを、効果の事前実測を待たずに実装する。

## 完了条件

単体テスト、静的API照合、Releaseビルド、同梱DLLと配布ZIPの確認、差分レビューで終了する。実ゲーム起動、プレイ、性能測定は今回の工程に含めない。実作業で成立しなかった接続は、欠けた型・設定形式・キャッシュ契約を具体的に記録して独立作業を進める。PR #5へ結果を積み、公開・マージは別の指示に従う。
