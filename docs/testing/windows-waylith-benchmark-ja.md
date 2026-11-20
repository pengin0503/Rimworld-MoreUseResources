# Windows版 RimWorld 1.7 の実機測定手順

対象は Windows 11、RimWorld 1.7.5011 rev24（Waylith）、MoreUseResources の PR #5 です。想定機は Ryzen 7 9700X、RX 7800 XT 16 GB、RAM 32 GB、2560×1440／144 Hzです。各機能を個別に切り替え、起動、TPS、フレーム時間、RAM、VRAMのどこに効果が出るかを測ります。

> このガイドは利用者が別途実測するときの資料です。[今回の実装の完了確認](../superpowers/plans/2026-11-17-performance-mod-resource-coexistence-ja.md)は単体テスト・静的確認・Releaseビルド・配布物検証で行います。

## 1. 測定環境を固定

1. PR #5 の成功した GitHub Actions から `Rimworld-MoreUseResources` を取得し、ZIPを展開して同じコミットのDLLを使います。対象コミット、RimWorldのビルド番号、AMDドライバー版、Windowsビルドを記録します。
2. 現在の `ModsConfig.xml` と各MOD設定、検証に使うセーブを別フォルダへコピーします。セーブは検証用の別名にし、毎回同じコピーを読み込みます。オートセーブは全条件で同じ設定にし、TPS／フレーム計測の最中には発生させません。
3. Harmony、WOWGAG's Performance Patch、Axen's Performance Enhancements、Overclockをいつものロード順で有効にします。各バージョンと並列処理オプションの状態を記録します。初回セットではOverclockとWOWGAGの実験的な並列Pawn処理をOFFにして固定し、普段それらを使う場合は別セットで実施します。
4. 解像度、表示モード、VSync、FPS制限、ゲーム速度上限、カメラ位置、ズーム、AMDの電源／クロック設定を固定します。Steamのダウンロードや録画など、試行ごとに変動する負荷を避けます。FPS／TPSオーバーレイを使う場合は全条件で同じものを表示します。
5. 結果フォルダを `日付-コミット/条件/試行番号` に分けます。起動ごとに `Player.log` を別名でコピーします。実際のユーザーデータフォルダはゲームの設定から開けます。

## 2. 比較条件と繰り返し

| 条件 | 設定 | 確認すること |
| --- | --- | --- |
| A | 3つのパフォーマンスMOD有効、MoreUseResources無効 | 併用構成の基準 |
| B | 同じ構成でMoreUseResourcesを有効にし、機能を既定値に戻す | 既定設定の影響 |
| C | Bから「実アセット積極活用プリセットを適用」 | ワーカー、RAM常駐、VRAM先読みを合わせた影響 |
| D | Cから測りたい項目を1つだけ変更 | 個別機能の寄与 |

まずB/Cを各3回、差が小さい場合は各5回。実行順は B→C、C→B、B→C と交互にします。AはMOD追加全体の影響を見たい場合に各3回測ります。Aでは本MODの内部ログや状態表示が取れないので、外部測定と手動計時を共通の指標にします。

条件ごとに先に一度起動してキャッシュを生成し、タイトル到達後に正常終了します。以降のゲーム内比較は同じ温まったキャッシュ条件で行います。キャッシュの初回生成やWindowsの再起動直後は別の行に記録します。

### 併用変更前に採っておく比較

基準のDLLはPR #5の `94ed10a` とし、A（3 MODのみ）とC（現行の実アセット積極活用）の組み合わせを同じセーブで各3回記録します。詳細は[併用設計](../superpowers/specs/2026-11-17-performance-mod-resource-coexistence-ja.md)を参照してください。

| 事前確認 | 現行版で行う操作 | 比較できる結果 |
| --- | --- | --- |
| WOWGAGのXML読み込み省略 | CでWOWGAGのパッチ結果再利用をON、MoreUseResourcesのXMLキャッシュをOFFに固定。MoreUseResourcesの「ロード工程の所要時間を記録する」だけをON／OFF切替。各条件で同じMOD構成の初回起動を済ませ、次回起動から測る | WOWGAGの再利用とXML読み込み省略の状態、外部計時のタイトル到達秒数。計時OFFの条件も外部計時を使う |
| 並列TickとCPU枠 | Cの通常・高速時を、自動と最大4ワーカーで比較。最初は外部の並列Pawn TickをOFFにする。別セットでは使う並列処理を一つだけONにし、起動ログで実際に適用されたことを確認する | ワーカー枠、実効速度、TPS、P95／P99、CPU使用率 |
| 3 MODが重なる機能 | WOWGAG・Axen・Overclockの起動ログを保存し、予約、ガス、ワールド更新、並列Pawn Tickに関する有効・迂回・解除の記録を拾う | 設定と実際の適用の差。該当箇所を確定できないログは「判定不能」と記録 |

## 3. 起動時間、先読み、GPU圧縮

1. ゲーム終了を確認し、同じ方法で起動します。ストップウォッチは起動操作の直前に開始し、タイトルが表示されて操作できた時点で止めます。起動前の先読みを含めて比較する場合はスクリプトを実行する直前から計ります。
2. B/C/Dでは `Player.log` の `[More Use Resources] Load timing:` 行を保存します。`process start to mod`、`mod to loaded`、ピークワーキングセット、XMLやアトラスの各工程を記録します。内部計時は先読みスクリプトの実行前半を含まず、工程には入れ子があるため個別工程の合算は使いません。
3. **パック先読み**は同じ `-Profile Default` と同じエンコード方式で、`-StartupPackManifest` だけを切り替えます。判定はスクリプト開始からの総時間で行い、`Startup pack warmup read ...` のMiBと秒数も記録します。0 MiBや古い一覧の警告が出た回は先読み成功の試行から外し、理由を残します。CPU方式とGPU方式には各方式で生成した一覧を用意します。
4. **GPU圧縮**は同じ環境・GC設定で `-GpuTextureEncoding` だけを切り替えます。比較用のシェルで継承された `WAYLITH_LOAD_ENCODE` を確認し、基準側のプロセスではGPU指定がないことを確認します。別のキャッシュディレクトリを使うので、同じ種類の「生成あり／生成済み」を比較します。生成ありの試験では、ゲームを閉じた状態で、対象方式のテクスチャキャッシュだけを別フォルダへ退避して始めます。Config、Saves、公式 `Assets.zip` は計測対象のキャッシュと別に扱います。
5. ロード時間が改善したかは外部の総時間と工程ログを照合します。パックI/Oが減ったかの確認には読み取りMiB、GPU圧縮では初回生成時間・キャッシュ容量・描画を記録します。

```powershell
$game = 'D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.exe'
$launcher = '.\tools\Start-RimWorldGc.ps1'
$manifest = 'D:\RimWorldUserData\MoreUseResources-StartupPacks.json'
& $launcher -GameExecutable $game -Profile Default
& $launcher -GameExecutable $game -Profile Default -StartupPackManifest $manifest
& $launcher -GameExecutable $game -Profile Default -GpuTextureEncoding
```

3行は別々の試行で実行します。各試行の後でゲームを終了します。

## 4. セーブ内の実行時間とリソース使用量

1. 同じセーブをロードし、同じマップ・カメラ・ズームへ移動してポーズした状態で30秒待ちます。ロード確定から操作可能までの時間も別に記録します。
2. **通常速度90秒**、**高速90秒**、**ポーズ中に決めたカメラ経路を1周**の3種類を分けます。各試行は元のセーブからやり直し、例えば「本拠点の在庫→作業場→遠征先→本拠点」と、切替の秒数を固定します。計測途中に設定画面を開かず、FPS・TPS欄の値は画面録画を使う全条件で同じように採取するか、各測定終了時に記録します。
3. 戦闘の強制等速化やオートセーブが入ったら時刻をメモし、固定速度の試行からは外します。負荷が上がった場面でワーカー枠を抑えるかの試験は別に行い、前・中・後のワーカー枠・TPS・P95を採ります。
4. タスク マネージャーの「パフォーマンス」で空きRAMと専用GPUメモリを開始前・終了時に記録します。MOD状態欄ではドライバー予算、GPU先読み件数、需要優先件数、常駐パック量を採ります。ワーキングセット、プロセスのPrivate Bytes、OS空きRAM、VRAMを別々の列にし、共有マップを二重に加算しません。

### フレーム時間をCSVで取得

Windows版 [PresentMon CLI](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md) を使う場合の例です。ゲームPIDはタスク マネージャーの「詳細」または起動スクリプト出力で確認します。`$PID` はPowerShell自身のPIDなので、別の変数名を使います。

```powershell
$gamePid = 12345
& .\PresentMon.exe --process_id $gamePid --delay 8 --timed 90 --terminate_after_timed --v1_metrics --output_file .\C-fast-01-frames.csv
```

コマンド実行後の8秒以内にゲームへ戻り、毎回同じ手順で対象速度にします。CSVの `MsBetweenPresents` を全条件で使います。主画面の `SwapChainAddress` を揃え、試行の中央値・P95・P99と、50 msを超えた区間数を記録します。CLIを使えない場合はMOD画面の平均・P95の表示で比較し、CSV測定と表を分けます。

### CPUとRAMを毎秒記録

同じPowerShellで、各試行に同じ時間だけ実行します。CPUは全論理CPUに対する割合です。

```powershell
$gamePid = 12345
$clock = [Diagnostics.Stopwatch]::StartNew()
$previousCpu = (Get-Process -Id $gamePid).CPU
$previousTime = $clock.Elapsed.TotalSeconds
$rows = for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Seconds 1
    $gameProcess = Get-Process -Id $gamePid -ErrorAction SilentlyContinue
    if ($null -eq $gameProcess) { break }
    $now = $clock.Elapsed.TotalSeconds
    $cpu = $gameProcess.CPU
    [pscustomobject]@{
        Utc = [DateTime]::UtcNow.ToString('o')
        TimeSeconds = [Math]::Round($now, 2)
        CpuPercent = [Math]::Round(100 * ($cpu - $previousCpu) / (($now - $previousTime) * [Environment]::ProcessorCount), 2)
        WorkingSetMiB = [Math]::Round($gameProcess.WorkingSet64 / 1MB, 1)
        PrivateMiB = [Math]::Round($gameProcess.PrivateMemorySize64 / 1MB, 1)
    }
    $previousCpu = $cpu
    $previousTime = $now
}
$rows | Export-Csv .\C-fast-01-cpu-ram.csv -NoTypeInformation
```

## 5. 個別機能と互換性の確認

| 項目 | 操作 | 記録 |
| --- | --- | --- |
| RAM常駐 | 既存パックが常駐後、その量より小さく上限を下げる。次に上限を戻し、RAM解放と通常の再開も確認 | 各操作前後の常駐MiB、パック数、空きRAM |
| VRAM先読み | 先読みが予算で止まる状態を確認し、同じゲーム内で予算上限または最低空き量を調整して余裕を作る。再走査0でも未走査候補が再開するかを見る | 使用MiB、予算MiB、保留・失敗・アップロード数、P95 |
| 異方性フィルタとグラフィック事前構築 | 一度に1項目を切り替え、同じ位置・ズームで同じ地形や建物を表示。画質MODの検出状態も確認 | 画質の比較画像、アップロード／事前構築件数、状態欄、P95 |
| GC | `-Profile Workstation` で同一フェーズ・同一速度の基準を保存し、`-Profile Server` で起動。GC種別表示を確認し、同じ場所で安定した窓を3回以上記録 | GCの停止ms/分、実際のGC種別、回数、ヒープ・コミットMiB |
| JIT | WOWGAGのJITオプションをON／OFFにする条件を分け、MoreUseResourcesのJITをONにしてタイトル・ポーズで観察 | WOWGAG優先表示、JIT準備件数、初回操作のフレーム時間 |
| XMLキャッシュ | 同じ設定で2回起動し、結果表示とXML工程時間を記録。WOWGAGがXMLに介入する場合は迂回表示を確認 | ヒット／ミス／迂回の区別、XML工程秒数、欠損Defエラー |
| セーブと切替 | 検証用スロットへ手動保存、ロード、タイトルへ戻る。3つのMOD有効でマップ移動と10分以上の通しプレイ | 新規エラー、描画欠け、セーブ読込、VRAM圧迫やGC急増の時刻 |

## 6. 結果を判定して添付

- 各指標の試行中央値と最小～最大を比較します。`(新条件－基準) ÷ 基準 × 100` を計算し、TPSはプラス、フレーム時間とロード時間はマイナスが改善です。使用量だけが上がった場合は、速度改善と区別して記録します。
- 初回の目安として、TPSが5%以上低下、フレームP95が10%以上悪化、またはロード時間が測定のばらつき以上に増えた場合は、Dで項目を1つずつ戻して再計測します。3回で差がばらつきに埋もれる場合は5回に増やします。エラー・描画欠け・ロード失敗は性能集計と別に記録します。
- GitHubに結果を記録するときは、下の1行と、該当ログ、フレームCSV、CPU/RAM CSV、設定ごとのメモを添えます。ログのユーザー名・個人パスは置き換え、セーブの添付は再現に必要な場合だけにします。

| コミット | 条件・試行 | 起動／ロード秒 | TPS | P95／P99 ms | CPU% | WS／Private MiB | 空きRAM MiB | VRAM使用／予算 MiB | 備考 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
|  |  |  |  |  |  |  |  |  |  |
