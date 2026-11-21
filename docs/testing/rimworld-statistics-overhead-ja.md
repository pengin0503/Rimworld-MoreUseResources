# Rimworld Statistics の重さを測る（あり／なしの比較）

別 MOD の [Rimworld Statistics](https://github.com/pengin0503/Rimworld-Statistics)（以下 RS）を入れた起動と外した起動を、この MOD の「状態をコピー」で比べます。RS 側のコードは変えません。

RS の資料で未計測のまま残っている次の 3 点を埋めるのが目的です。

- ゲーム内のティックとフレームの時間（`docs/performance/2026-11-19-processing-resources.md` の「Frame time and TPS were not measured」）。
- RS の目標「TPS の中央値の低下 2% 以内」「セーブの追加時間 50 ms 以内または 5% 以内」（`docs/sdk/p3-game-acceptance.md`。未実施）。
- タイトル画面での取り込み中のなめらかさ（同じ資料の「its effect on the title screen's smoothness was not measured」）。

所要時間は、準備に 10 分、起動 3 回（準備・RS あり・RS なし）にそれぞれ 10〜15 分です。対象は Windows の RimWorld 1.7 です。

## 何を比べるか

コピーの中の次の行を、RS あり（A）と RS なし（B）で比べます。

| 行 | 見る値 | RS のどの処理か |
| --- | --- | --- |
| この起動中のプレイ（`speed 3 x6`） | `main thread in ticks …% (… ms per tick)` | ティックの中で動く RS の記録（Harmony パッチとゲームコンポーネント）。 |
| 同上 | `UI …% (… ms per frame)` | 毎フレームの UI（`Root.OnGUI`）で動く RS のパッチ（ショートカットキーの確認）と、RS のウィンドウの描画。 |
| 同上 | `main thread busy …%` | 上の 3 つの外で毎フレーム動く RS の処理（ゲームコンポーネントの `GameComponentUpdate` と、`UIRoot.UIRootUpdate` の 2 つの postfix）は、ここにだけ入ります。どれも、ふだんはフラグを読んで戻るだけの作りです。 |
| 同上 | `stopped … ms (…%)`、`allocating … MiB/s`、`max heap`、`max GC committed` | RS が確保するメモリと、それによる GC。 |
| 同上 | `CPU cores by thread` の `.NET TP Worker` | RS が裏で動かす処理。RS はスレッドプール（`Task.Run`）を使うので、ここに入ります。この MOD のスレッドの読み取りも同じ所に入りますが、A と B で同じです。 |
| 同上 | `frames over 50 ms`、`over 100 ms`、`longest` | 引っかかり。 |
| この起動中のセーブ | `median … ms`、各回の値、`GC stopped … ms and … MiB allocated per save` | RS がセーブに書くチェックポイントと、セーブ後の記録（どちらもゲームのセーブ処理の中で動きます）。 |
| この起動中のタイトル画面 | `FPS`、`frames over 50 ms`、`longest`、`.NET TP Worker`、`UI … ms per frame` | タイトル画面での取り込み（強制再検査）と、RS のウィンドウの描画。 |

TPS そのものは比べません。2026-11-20 の実測では速度 3 の TPS が目標の 100%（360 に対して 359.6）で、ゲームは次のフレームを待っていました。この状態では、RS が少し重くても TPS は変わらず、待ち時間が減るだけです。代わりに、ティック 1 回に使ったメインスレッドの時間（`ms per tick`）を比べます。これは待ち時間を含まないので、TPS が上限に張り付いていても差が出ます。

## 1. 準備（初回だけ）

普段のセーブと RS の記録に触れないよう、計測用のセーブデータフォルダーを別に作ります。RS の受け入れ手順（`docs/sdk/p3-game-acceptance.md`）と同じやり方です。

1. ゲームを終了しておきます。
2. 計測用のフォルダーを作ります。例：`C:\RWMeasure`。パスには空白と `=` を入れないでください（ゲームは `-savedatafolder=` の後ろを `=` で区切って読みます）。
3. エクスプローラーのアドレス欄に `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios` と入れて開き、`Config` フォルダーと `Saves` フォルダーを `C:\RWMeasure` にコピーします。`RimworldStatistics` フォルダー（RS の記録）はコピーしません。
   - `Saves` が大きすぎるときは一部で構いません。プレイに使う 1 個は必ず入れます。タイトル画面の取り込みは、ここに入れたセーブを読みます。
4. Steam のライブラリで RimWorld を右クリック →「プロパティ」→「一般」の「起動オプション」に `-savedatafolder=C:\RWMeasure` と入れます。**計測が終わったら必ず消してください**（消すまで普段のセーブが見えません）。
5. この MOD を最新の main に入れ替えます（[PC での短い計測](pc-quick-measurement-ja.md)の「1. MOD を入れる」と同じ手順です）。

## 2. 準備の起動

1. Steam から起動します。MOD 一覧は普段と同じです（`Config` をコピーしたため）。
2. タイトル画面で「オプション」→「MODオプション」→「More Use Resources」を開き、**リソース活用を有効化** を切ります。この MOD の先読みや検査が A と B で違う量だけ動くと、比べられなくなるためです。計測は切っても続きます。「Server GC でゲームを起動する」は今のまま（初期 ON）にします。
3. RS は初期設定のまま（タイトル画面での自動取り込み ON、処理に使う PC の資源は「標準」）にします。
4. RS の自動取り込みが終わるまで待ちます。「通算統計」を開き、「データ管理」タブの進行バーが消えれば終わりです。`C:\RWMeasure` に `RimworldStatistics` フォルダーができていれば、起動オプションが効いています。
5. ゲームを終了します。

この起動は計測しません。設定を整え、OS のファイルキャッシュを A と B で同じように温めるためのものです。

## 3. A：RS あり

1. Steam から起動します。
2. **タイトル画面**（ここから時間を計ります。スマートフォンのストップウォッチで構いません）
   1. タイトル画面が出たらすぐ「通算統計」→「データ管理」→ **強制再検査** →「確認」を押します。すべてのセーブを最後まで読み直します。
   2. 進行バーが消えるまで待ち、ウィンドウを閉じます。
   3. タイトル画面が出てから **2 分** たつまで待ちます。強制再検査が 2 分を超えたら、終わった時点までの時間を書き留めます（B で同じだけ待ちます）。
   4. この間、別のウィンドウに切り替えないでください。別ウィンドウの間はタイトル画面の平均から外れます。
3. **プレイ**
   1. `Saves` からコピーした、いつものセーブをロードします。
   2. ゲーム速度を 3（`>>>`）にして、ポーズせずに **5 分** プレイします。途中で自動セーブが入っても構いません（その区間は平均から外れ、セーブの記録には入ります）。
4. **セーブ**
   1. ポーズしてから、Esc →「セーブ」で **10 回** 続けてセーブします。名前は `計測A` などにして、2 回目からは同じ名前に上書きで構いません。元のセーブの名前には上書きしません（B でも同じセーブから始めるためです）。
5. Esc →「オプション」→「MODオプション」→「More Use Resources」→ 一番上の **状態をコピー** を押し、このスレッドに Ctrl+V で貼ります。コピーできないときは `C:\RWMeasure\MoreUseResources-Status.txt` を添付してください。

## 4. B：RS なし

1. タイトル画面に戻り、「MOD」で **Rimworld Statistics を外し**、閉じます。ゲームが再起動します。再起動しても起動オプションは引き継がれます（本体 1.7.5011 の再起動処理は、起動時の引数をそのまま渡します）。
2. **タイトル画面**：何もせずに A と同じ時間（2 分、または書き留めた時間）待ちます。別のウィンドウには切り替えません。
3. **プレイ**：A と同じ元のセーブをロードします。RS の記録が入ったセーブなので、未定義のコンポーネントについての通知が 1 回出ることがあります。閉じて続けます。速度 3 で 5 分プレイします。
4. **セーブ**：ポーズして、`計測B` などの名前で 10 回セーブします。
5. **状態をコピー** を押して貼ります。

## 5. 後片付け

1. Steam の起動オプションから `-savedatafolder=C:\RWMeasure` を消します。
2. `C:\RWMeasure` は消して構いません。普段のセーブデータフォルダー、MOD 一覧、RS の記録は変わっていません。
3. 普段の起動では、この MOD の「リソース活用を有効化」は普段のまま（`C:\RWMeasure` の設定を切っただけ）です。

## 貼られた値の読み方

| 比べる値 | 読み方 |
| --- | --- |
| `ms per tick`（A − B）÷ B | RS がティックに足した割合です。RS の目標「TPS の低下 2% 以内」は、TPS が上限に届かないほど重い場面でのこの割合に当たります。 |
| `UI … ms per frame`（A − B） | RS が毎フレームの UI に足した時間です。フレーム上限が 144 なら 1 フレームは約 6.9 ms です。 |
| セーブの `median`（A − B）と、その B に対する割合 | RS の目標「50 ms 以内または 5% 以内」と比べます。各回の値で 1 回目だけが遅いなら、それは最初の呼び出しのコンパイルです。 |
| セーブの `GC stopped` と `allocated per save` | RS がセーブ中に確保したメモリと、それで増えた GC の停止です。 |
| プレイ中の `allocating`、`stopped`、`max heap`、`max GC committed` | RS が常に使うメモリと、それによる GC です。 |
| プレイ中の `.NET TP Worker` | RS がプレイ中に裏で使う CPU です。事前計算は初期 OFF なので、ほぼ差がないはずです。 |
| タイトル画面の `frames over 50 ms`、`over 100 ms`、`longest`、`FPS` | 取り込み中の引っかかりです。B（何もしないタイトル画面）より多ければ、取り込みがメインスレッドを止めています。 |
| タイトル画面の `.NET TP Worker`、`process CPU` | 取り込みに使ったコア数です。RS の「標準」は論理 CPU の半分（最大 3 本）を使います。 |

差が数 % しかないときは、A と B を 1 回ずつでは雑音と区別できません。同じ組をもう 1 回取り、2 回の差の揃い方で判断します。2026-11-20 の 2 回（GC の種類だけが違う起動）では、ティックの割合が 25% と 26% でした。

## この手順で測れないもの

- **セーブのロード時間**：この MOD はセーブのロードにかかった時間を記録しません。RS はロード時にもチェックポイントを読みます。必要になったら、この MOD に記録を足します。
- **RS の処理だけの CPU**：RS の裏の処理はスレッドプールで動くので、ほかのプールの処理と分けられません。A と B の差で見ます。
- **ティック・マップ更新・UI の外の毎フレームの処理だけの時間**：RS の `GameComponentUpdate` と `UIRoot.UIRootUpdate` の postfix は、メインスレッドの使用率にしか表れません。使用率にはフレーム上限の待ちの最後の空回り（1〜2 ms）も入るので、小さな差は読めません。
- **自動取り込みの普段の量**：普段の起動の取り込みは、名前・サイズ・更新日時が変わったセーブだけを読みます。ここでは最も重い場合（強制再検査で全部を読み直す）を測ります。
- **RS の設定ごとの差**：「処理に使う PC の資源」を「多め」にした場合（取り込みのスレッド数が増え、優先度が通常より下がる）や、事前計算を ON にした場合は、その設定で A をもう 1 回取れば比べられます。

## 確認した前提（本体 1.7.5011 rev24 を逆コンパイルして確認）

- 手動セーブ（`Dialog_SaveFileList_Save`）も自動セーブ（`Autosaver.DoAutosave`）も、`GameDataSaveLoader.SaveGame(string)` を `LongEventHandler.QueueLongEvent(…, doAsynchronously: false)` でメインスレッドから呼びます。この MOD はこのメソッドの最初の prefix（`Priority.First`）から最後の finalizer（`Priority.Last`）までを 1 回のセーブとして計ります。ほかの MOD の prefix と postfix も含みます。
- RS は `SafeSaver.Save` に prefix・postfix・finalizer を付けています（`CheckpointSavePatch`、`SaveCompletedPatch`）。`SafeSaver.Save` は `GameDataSaveLoader.SaveGame` の中で呼ばれるので、RS のセーブ中とセーブ直後の処理は計った時間に入ります。
- 1 フレームの中で、`Game.UpdatePlay` は `TickManagerUpdate`（ティック）、`MapUpdate`（マップ更新）の後で `GameComponentUtility.GameComponentUpdate` を呼びます。このため RS の `GameComponentUpdate` は、ティックにもマップ更新にも入りません。RS のショートカットキーのパッチ（`UIRoot_Entry.UIRootOnGUI`、`UIRoot_Play.UIRootOnGUI`）は `Root.OnGUI` の中なので UI に入ります。
- セーブ中の GC の停止は `GC.GetTotalPauseDuration()`、確保量は `GC.GetTotalAllocatedBytes(false)` の差です。確保量は全スレッドの合計ですが、セーブ中はメインスレッドがゲームを止めているので、ほとんどがセーブの分です。
- `-savedatafolder=` は `GenFilePaths.SaveDataFolderPath` が読みます。`Config`（MOD 一覧と MOD 設定）、`Saves`、この MOD の状態ファイル、RS の記録は、すべてこのフォルダーの下に作られます。
- MOD 一覧を変えたときの再起動（`Waylith.RestartManager`）は、起動時の引数を `-WaitForExitingPid` 以外すべて新しいプロセスに渡します。
- タイトル画面の平均は、フェーズが「タイトル画面」のまま 10 秒の区間が丸ごと続き、ウィンドウにフォーカスがあった区間だけを足します。プレイ中の平均と同じ規則です。
- 「リソース活用を有効化」を切っても、FPS・TPS・GC・CPU・セーブ時間の計測は止まりません（計測は設定に関係なく動きます）。
- 1.7.5014 rev1195 の本体 DLL は R2 にないため、この版での確認はしていません。参照パッケージ（Krafs.Rimworld.Ref 1.7.5014-unstable）で、この MOD が使う型とメソッドの宣言が 5011 と同じであることは確認済みです（[PC での短い計測](pc-quick-measurement-ja.md)）。
