# 他MOD併用とバニラ処理追加の静的検証記録（2026-11-17）

対象: RimWorld Windows 1.7.5011 rev24 / .NET 10 / Waylith。実ゲームの起動と性能計測は行っていません。ここにあるのは逆コンパイルによる照合、単体テスト、ビルドと配布物の確認です。

## 計画からの変更

[実装計画](../superpowers/plans/2026-11-17-external-mod-resource-allocation-ja.md)のうち、次の項目は依頼者の指示により実装していません。

- 外部MODの並列機能が有効なときに本MODのワーカーを最大4に抑える。
- Axenのパッチがあり共有ジョブプールが混んでいるときに本MODのワーカーを最大1に抑える。
- 上記の抑制からの復帰処理。

`CompatibilityPolicy.LimitWorkers` は外部MODの状態と共有プールの状態を受け取りますが、値を変えずに返します。上限を下げるのは本MOD自身のフェーズ設定、ワーカー上限、適応制御、フレームガードだけです。

## 確認済みの事実と根拠

| 項目 | 確認した内容 | 根拠 |
| --- | --- | --- |
| WOWGAGのワーカー設定 | MODクラス `GppMod17`、設定クラス `GUIPerformancePatch.GppSettings`、`parWorkers` は整数、保存名 `parWorkers`、初期値0（0は保存されない）、設定UIは0〜32。0は物理コア数−1を基準にした自動。値が現在のプールと違うとプールを作り直す。`GppParallel.WorkerCount` が稼働数を公開 | Workshop 3797541313 の1.7用DLLを逆コンパイル |
| WOWGAGの並列ON／OFF | `parMapPost`、`parPawnTick`、`parPawnComps`、`parPredictConst`、`parPredictFail`、`parPredictJob`、`parWork`、`parWorldPath`。Axen導入時は自身の並列処理を止める（`foreignBlockedState`） | 同上 |
| WOWGAGの区画関連パッチ | `MapDrawer.MapMeshDrawerUpdate_First` へのトランスパイラー、`SectionLayer_Things.Regenerate` などへのパッチ。`DrawSection` などへは計測用のvoid prefix | 同上 |
| Axenの経路探索 | `PathFinder.ScheduleGridJobs` へのprefix。専用のワーカー設定は公開ソースにない | 公開ソースの `PathDag.cs` |
| 共有ジョブプール | `Waylith.Jobs.WaylithJobRuntime.HasPendingWork` と `WorkerCount` は公開static | 本体のWaylith DLLを逆コンパイル |
| シェーダーキャッシュ | `<CacheDataPath>/ShaderCache/<SHA-256の64桁小文字>.spv-cache` を `File.ReadAllBytes` で読む。使用した記録の更新日時を1日ごとに更新し、30日未使用で削除 | `RuntimeShaderCompiler`、`RimWorldWaylith.Bootstrap` |
| メッシュの転送 | `UnityEngine.Mesh` は互換データを保持し（`_compatDirty`）、初回の `Mesh.EngineMesh` 参照で変換する。変換は `MarkCpuStorageChanged` → `UploadRequested` → `MeshUploader.Enqueue` と進み、`pendingMeshSet` で重複を除く。キューは `MeshDrawSubmission` で処理される | Waylith DLL、UnityStubs |
| 区画の再生成 | 画面外で変更された区画は `anyLayerDirty` が立つだけで、`Section.DrawSection` が見えたときに `RegenerateDirtyLayers` を呼ぶ | `Verse.Section`、`Verse.MapDrawer` |
| ポートレート | `PortraitsCache.CachedPortrait.Expired` は最終使用から1秒。パラメーター完全一致のときだけ再利用 | `RimWorld.PortraitsCache` |
| MOD設定ファイル | `Config/Mod_<フォルダ名>_<MODクラス名>.xml`、ルート `SettingsBlock`、子 `ModSettings Class="…"`。既定値は保存されない | `Verse.LoadedModManager`、`Verse.Scribe_Values` |

## 追加した処理と、重複を避ける条件

| 追加した処理 | 再利用される経路 | 重複登録を避ける条件 |
| --- | --- | --- |
| 区画メッシュの変換と転送の前倒し | 本体の描画が同じ `Mesh` を使う。転送はエンジン自身のキュー | `_compatDirty` が真のメッシュだけ `EngineMesh` を参照する。変換済み・転送待ちには触れない。`UploadMeshData` は呼ばない |
| 画面外区画の先行更新（初期OFF） | 本体の `RegenerateDirtyLayers` がフラグを下ろし、後の描画は結果を使う | フラグが立っている区画だけ。ポーズ中、現在のマップ、1フレーム1区画 |
| シェーダーキャッシュの先読み | OSファイルキャッシュ。エンジンは同じファイルを通常の読み取りで読む | 名前の形が一致するファイルだけ。既存のOS先読みに追加し、RAMコピーとアセット検査の対象外 |
| コピー予算不足時のOS先読み | OSファイルキャッシュ | 同じ世代で1ファイル1回。読み取り条件が外れたら中断 |
| テクスチャ需要履歴 | 既存のGPU先読みの順番だけを変える | 現在読み込まれているテクスチャに解決できたキーだけ。常駐済みなどの既存の除外条件はそのまま適用 |
| 起動前のセーブ先読み | OSファイルキャッシュ。本体のロードが同じファイルを読む | ユーザーが指定した1ファイルだけ |

## 譲渡の判定

他MODのパッチが「置き換え」に当たるのは、トランスパイラー、または戻り値がvoidでないprefixです。void prefix、postfix、finalizerは結果を変えないものとして扱い、譲渡の理由にしません。この判定は静的な分類であり、void prefixが内部状態を書き換えて実質的に処理を変える場合は検出できません。

## 確認できなかったこと（不足しているAPI・キャッシュ契約）

- **Overclockの配布DLLを入手できていません**（R2にあるのはWOWGAGだけです）。`OverclockSettings.parallelThreads` の保存名・型・範囲・反映時期、`Overclock.Optimisations.ParallelPawnTicks.active`、パッケージID `soap.overclock` は設計資料の記載に基づきます。そのため起動スクリプトは、ファイルに値がすでに書かれている場合だけ変更し、ゲーム内表示は読み取れない場合に「判定不能」とします。
- **Axenの配布DLLを入手できていません**。確認できたのは公開ソースの `PathDag.cs` だけです。HarmonyオーナーIDが `axeno7.performanceplus` で始まることは設計資料の記載に基づきます。一致しない場合も、経路グリッド発行メソッドに登録された全オーナーを表示します。
- **テクスチャのアップロードを他MODが担当しているかを判定する契約がありません**。`TextureUpload` を譲る条件は実装していません。
- **需要履歴の指紋はパックのサイズと更新日時を含みます**。エンジンが起動のたびにキャッシュZIPを書き換える構成では、履歴が毎回無効になります。実機でしか確認できません。
- **メッシュ転送によるVRAM増加量**と、既存のテクスチャ用ヒープ予算の確認がメッシュ分を反映するかは未計測です。

## 実機で未計測のこと

CPU・RAM・GPU・VRAMの実際の使用量の変化、FPS・TPS・ロード時間・初回表示時のフレーム時間への影響、他MOD3種を同時に入れたときの動作、ゲーム内状態欄の表示、起動スクリプトによる設定変更が各MODに反映されることは、いずれも実機で確認していません。

## 検証の実行結果

- `dotnet test Tests/MoreUseResources.Tests.csproj -c Release`: 236件成功。
- `pwsh -NoProfile -File tools/Test-StartRimWorldGc.ps1`: 120項目成功（一時ファイルのみ、ゲーム起動なし）。
- 変異確認: ワーカー上限を外部MOD有効時に4へ抑える変更を入れると8件のテストが失敗すること、起動スクリプトの範囲確認とクラス確認を外すと検証が失敗することを確認しました。
- Releaseビルドの出力と同梱 `Assemblies/MoreUseResources.dll` のバイト一致、配布ZIPの内容確認。
- 差分レビューは実装者自身によるものです。別の担当者によるレビューは行っていません。
