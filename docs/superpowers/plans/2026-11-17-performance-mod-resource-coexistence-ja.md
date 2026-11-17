# 軽量化MODとMoreUseResourcesの併用実装計画

> 後続の実装担当は、タスクごとの手順を `superpowers:executing-plans` または `superpowers:subagent-driven-development` で進める。各タスクでテスト結果を確認してから次へ進む。

**目標**

WOWGAG・Axen・OverclockによるTick・GUI・ロードの高速化を活かし、ゲーム本体が既に行うロード・描画・ランタイム処理にCPU・RAM・VRAMを追加で使えるようにする。

**構成**

起動時のXML互換判定とロード後の並列処理判定を分ける。外部MODの情報はゲームスレッドで読み、既存のワーカープール、OSファイル先読み、WaylithのGPU先読みに純粋な値を渡す。

**対象環境**

Windows版RimWorld 1.7.5011 rev24、.NET 10、Waylith、Harmony 3。実装基準はPR #5の最新コード。配布ZIPを静的に照合した基準コミットは `94ed10aebe842ae396a1c976e8bf1ddccb0f9210`。

**設計**

[併用設計と確認済みの重複](../specs/2026-11-17-performance-mod-resource-coexistence-ja.md)・[既存処理への追加リソース活用](2026-11-17-game-state-resource-work-ja.md)

## 共通条件

- 外部MODを任意依存として扱う。参照は既知の公開設定、型が一致する状態値、Harmonyのパッチ情報に限定する。
- MoreUseResources側の処理だけを調整する。ゲーム進行、セーブ形式、他MODの設定・適用済みパッチは外部MODと本体が管理する。
- バニラローダー、描画、ランタイムが使う処理にリソースを配分する。実装は既存処理の先行、キャッシュの保持、既存処理方式の選択と、そのための制御・互換性対応に限定する。静的に処理が成立する候補は実測での改善が未確認でも実装する。セーブ・停止・空きRAM・GPU予算は既存の制御に従わせる。
- ユニットテストは版違い・設定状態・予算境界を検証する。実ゲームの起動、プレイ、4 MOD併用確認、性能測定を今回の実装手順に含めない。
- 各タスクの実装前後は下記のテストを対象クラスに絞り、全体のReleaseビルドと配布物確認は最後に実行する。

```bash
dotnet test Tests/MoreUseResources.Tests.csproj -c Release --filter FullyQualifiedName~CompatibilityPolicyTests
```

## レビュー重点

| 場面 | 確認する挙動 |
| --- | --- |
| WOWGAG導入済みで起動時の設定未初期化、型不一致 | XML読み込み省略に関わるフックを譲り、残る工程を計時する（タスク1） |
| 並列設定ONでも別MODに阻止されている、又は動作中にOFFにする | 有効状態を判定できれば不要なCPU上限を解除し、判定不能を画面に出す（タスク2） |
| 並列時にワーカー設定が1、適応制御・フレームガードが作動、又は手動停止 | 既存の少ない上限、停止、RAM専用時の作業枠を保つ（タスク2） |
| ネイティブコピー予算だけが不足、又は空きRAM・世代の条件も失う | 前者のみOS先読みを許可し、予算回復時にコピー再試行を維持する（タスク3） |
| 同名テクスチャを複数MODが提供、MOD更新、破損した履歴ファイル、GPU予算不足 | 誤ったテクスチャを優先せず、現セッションで予算を満たす候補から通常処理を継続する（タスク4） |

## タスク1　WOWGAGのXML高速化を優先する

**ファイル**

`Source/Core/CompatibilityPolicy.cs`、`Source/Game/WowgagCompatibility.cs`、`Source/Game/ResourceRuntime.cs`、`Source/Game/LoadTiming.cs`、`Source/Game/MoreUseResourcesMod.cs`、日英の `Languages/*/Keyed/MoreUseResources.xml`。テスト追加先は `Tests/CompatibilityPolicyTests.cs`。

**境界**

Coreに `ExternalFeatureState`（`Absent`、`Inactive`、`Active`、`Unknown`）と `CompatibilityPolicy.YieldXmlToWowgag(ExternalFeatureState): bool` を置く。Game側は `WowgagCompatibility.ReadXmlReuseState(): ExternalFeatureState` を起動時に呼び、`LoadTiming.Install(Harmony, bool yieldXmlLoadStages)` とキャッシュフックの登録判断へ同じ値を渡す。

- [ ] `XmlReuseYieldsOnlyForActiveOrUnknown` を追加する。4状態の結果は順に `false, false, true, true`。
- [ ] 上記フィルターでテストが未実装により失敗することを確認する。
- [ ] 既存のパッケージID照合とWOWGAG設定インスタンス取得を再利用し、`reusePatchResult` の型が `bool` と確認できたときだけ値を採用する。JITの3項目の判定は従来のまま使う。
- [ ] 優先時は `LoadModXML` と `CombineIntoUnifiedXML` の計時フック、および `PatchedXmlCache.Install` を省く。残りの工程の計時、ロード完了ログ、`RecordLoadTiming` の起動時設定を維持する。省略理由を1回ログと状態欄へ出す。
- [ ] 同じテストを再実行し成功を確認する。対象の2メソッド以外のフック、起動全体の計時、既存JIT判定を差分で照合してコミットする。

## タスク2　外部の並列処理を診断し、プレイ中のCPU枠を調整する

**ファイル**

タスク1の `CompatibilityPolicy.cs` とテストに追加。Game側の新規 `Source/Game/PerformanceModCompatibility.cs` は状態読み取りとHarmony診断に限定する。接続先は `ResourceRuntime.cs`、`Source/Core/ResourceController.cs`、設定画面と日英のキー。既存のワーカープールと適応制御を再利用する。

**境界**

`CompatibilityPolicy.LimitWorkers(int requested, GamePhase phase, ExternalFeatureState wowgag, ExternalFeatureState overclock): int` は通常・高速時にどちらかが `Active` 又は `Unknown` の場合だけ `Math.Min(requested, 4)` を返す。コントローラーへの受け渡しは `SetExternalParallelism(ExternalFeatureState wowgag, ExternalFeatureState overclock)` のスレッド安全な1スナップショットとし、両MODの状態を同じタイミングで扱う。

- [ ] テストを追加し失敗を確認する。16論理CPU相当の要求14で、通常／高速・外部稼働時は4、判定不能時も4、確認済み無効／未導入時は14、タイトル・ポーズは14。要求0、1、2は増やさない。稼働から無効に切り替わった次の制御更新で外部上限を解除する。
- [ ] OverclockはパッケージID `soap.overclock` と既知の `ParallelPawnTicks.active` の型を確認する。WOWGAGは `parMapPost`、`parPawnTick`、`parPawnComps`、`parPredictConst`、`parPredictFail`、`parPredictJob`、`parWork`、`parWorldPath` を候補として、既知の機能別の装着・停止条件で実状態を判断する。設定ONでも確認済みのブロックなら無効とし、判別できない場合は判定不能とする。瞬間的なワーカー稼働深度には依存しない。
- [ ] ロード完了後に状態を取得し、外部MODの設定変更を拾えるよう、ゲームスレッドで約2秒間隔に更新する。重いメソッド列挙は設定ポーリングから外す。ロード・ホットリロード完了時に、予約・ガス・ワールド更新・Axenの経路グリッド・XML工程の実登録オーナーを対象メソッドから取得する。診断にはパッチ登録と並列稼働を別の列で表示する。
- [ ] `ControlTick` のフェーズ倍率・適応制御の後に上限を反映する。フレームガード、手動停止、セーブとフォーカス条件、RAM単独の読み取りに必要な枠を保つ。次回のジョブ取得から反映し、実行中処理の中断は既存の区切りに従う。
- [ ] フィルター付きテストと既存の `ResourceExpansionTests` を実行する。状態変化時だけログを更新すること、外部パッチへの書き込みがないことを差分で照合しコミットする。

## タスク3　ネイティブコピーの予算不足時もOSキャッシュを使う

**ファイル**

`Source/Core/FileCache.cs`、既存の `FileCacheTests` がある `Tests/MemoryTests.cs`、`README.md` のOS先読み説明。公開APIは既存の `FileCache.Pump` と状態カウンターを維持する。

- [ ] `RamCopyBudgetMissWarmsOsCacheAndRetriesCopy` を追加し、現行ではOS先読み成功件数が増えず失敗することを確認する。小さな実ファイルとテスト用時計を使い、定期再先読みを0にしても、コピー予算が足りない時点でOS先読み成功1・コピー0・保留1、時計を30秒以上進め、予算を64 MiB以上増やした後でコピー1になることを確認する。
- [ ] `ReadOne` は `TryBeginCopy` が予算不足で `deferred=true` を返した場合に限り、既存のOS専用読取経路へフォールバックする。OS先読み完了を `_warmed` にだけ記録し、`_done` はネイティブコピー成功時に限る。保留数と既存クールダウンを保つ。
- [ ] 読み取り権限がなくなった場合、ファイル世代が切り替わった場合、ガバナー採取中の一時閉鎖では通常の中断／再試行に従う。コピー保留のままサイズ・間隔に変化がなければ、同じファイルの即時連続読み取りを抑える。
- [ ] 対象テストと既存のOS再先読み・古いコピー解放・容量制限テストを実行する。容量不足と許可失効の挙動を説明に反映してコミットする。

## タスク4　実プレイで再読み込みされたテクスチャを次回も優先する

**ファイル**

新規 `Source/Core/TextureDemandHistory.cs` と `Tests/TextureDemandHistoryTests.cs`。接続先は `Source/Game/TexturePrewarm.cs` と `ResourceRuntime.cs`。既存の需要優先設定を入口にし、`README.md` と日英の状態表示を更新する。

**境界**

`TextureDemandHistory` はスキーマ1、互換指紋、最大512件の順位を持つ。型は `TextureDemandKey(string PackageId, string ContentKey)` と `TextureDemandSample(TextureDemandKey Key, int Count)`。APIは `Load(string path, string fingerprint): IReadOnlyList<TextureDemandSample>` と `Save(string path, string fingerprint, IReadOnlyList<TextureDemandSample> ranked): void`。純粋なストアに限定し、ゲームオブジェクトは渡さない。

- [ ] テストを追加して失敗を確認する。順位の往復、上限512件・1 MiB、破損、バージョン違い、互換指紋違い（配置順・パックの更新）、同じキーを持つ別MOD、絶対パスを拒否する入力を使う。履歴を読めないときは空の順位とする。
- [ ] ストアは1ファイルを一時ファイルから置換し、同じキーの重複と不正な値を除外する。順序付きのMOD ID・正規化したルート識別子・現行ビルド・Waylithのキャッシュ方式とインストールID・現行パックのパス識別子とサイズ及び更新日時をハッシュ入力にする。パックは `PackSourceSnapshot` からワーカーで列挙し、パック一覧の順序を正規化する。安定キーと互換指紋が取れた場合だけ過去履歴を使い、絶対パスは出力しない。
- [ ] ロード完了後、ワーカーで履歴を読み、DLL上の `Texture2D` はゲームスレッドで各ホルダーから分割解決する。解決済みの履歴候補を通常候補より先に既存の先読みループへ渡し、候補を消費する予算判定は通常走査と共有する。候補なし・古い候補・ロード失敗なら既存の巡回を進める。
- [ ] 既存の自己読み込み差し引き後の観測から上位512件を作る。保存は実需要が観測され、前回の保存から60秒以上経過し、順位情報が変わった場合に1ジョブだけ予約する。未完了の同一書き込みを重複予約しない。ホットリロード・無効化・終了時は旧世代の結果反映とファイル置換を無効にする。書き込みは既存のセーブ抑制と停止状態に従う。
- [ ] `TextureDemandHistoryTests` と既存GPU関連テストを実行する。Atlas除外、ドライバー予算、次回再読み込みの優先順がどの入口でも共有されることをレビューしてコミットする。

## タスク5　バニラローダーのセーブ読み込みを準備する

[既存処理への追加リソース活用](2026-11-17-game-state-resource-work-ja.md)に沿って、指定セーブを起動前にOSキャッシュへ載せる。バニラのロード経路が読み取るファイルを対象とし、既存ランチャーの先読み予算を共有する。

## タスク6　実ゲームを使わずRelease検証を行う

- [ ] .NET SDK 10.0.100で、全単体テストとReleaseビルドを行う。新規設定の読み書き、日英の設定キー、外部MODの有無と未確認版の分岐、ファイルの世代失効を確認する。
- [ ] 同梱DLLをReleaseビルドから同期し、同梱DLL比較とZIP生成を確認する。PowerShellを変更した場合は構文とファイル先読みの異常系を検査する。

```bash
dotnet test Tests/MoreUseResources.Tests.csproj -c Release
dotnet build Source/Game/MoreUseResources.csproj -c Release
cmp Source/Game/bin/Release/net10.0/MoreUseResources.dll Assemblies/MoreUseResources.dll
python3 tools/package_mod.py dist/Rimworld-MoreUseResources.zip
```

[Windows実機測定ガイド](../../testing/windows-waylith-benchmark-ja.md)は、利用者が別途比較する際の資料として残す。
