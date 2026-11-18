# Google Cloud VM でのベンチマーク実行手順

MoreUseResources の Core コード（`Source/Core`）を、ゲームを起動せずに Linux 上で直接動かして測ります。対象は CPU ワーカーの並列化、実アセットの SHA-256 索引化と重複検出、XML 解析とテクスチャ検査、ファイル探索・先読み・定期再走査、OS キャッシュと RAM コピー保持、メモリマップによるパック常駐、GC、停止・再開、メモリ不足、I/O 障害、長時間実行時のリーク確認です。結果は CSV と JSON に保存し、日本語の分析（`analysis.md`）を自動で作ります。

FPS・TPS・ゲーム内ロード時間は測れません。Windows の I/O、Unity/Waylith のメインスレッド、他MODの実際の並列処理も再現しません（最後の節を参照）。設計と各シナリオの中身は [設計書](../superpowers/specs/2026-11-18-gcp-benchmark-design-ja.md) にあります。

## 1. VM を用意する

想定は Ubuntu 24.04 LTS、8 vCPU / 32 GiB、ディスク 50 GB 以上、GPU なしです。作成例（名前・ゾーン・マシンタイプは適宜変更）:

```bash
gcloud compute instances create mur-bench --zone=asia-northeast1-b \
  --machine-type=n2-standard-8 --image-family=ubuntu-2404-lts-amd64 --image-project=ubuntu-os-cloud \
  --boot-disk-size=50GB --boot-disk-type=pd-balanced
gcloud compute ssh mur-bench --zone=asia-northeast1-b
```

e2 系は CPU が他テナントと共有されるため値がぶれます。比較に使う測定は n2 / c3 などの専有 vCPU のマシンタイプで行い、比較する 2 回は同じマシンタイプにします。

## 2. セットアップ

```bash
git clone https://github.com/pengin0503/Rimworld-MoreUseResources.git
cd Rimworld-MoreUseResources
git checkout claude/project-thread-8c4csj
tools/benchmark/setup-ubuntu.sh --delayacct
```

`setup-ubuntu.sh` は足りないもの（.NET 10 SDK、python3-boto3、taskset など）だけを apt で入れます。.NET SDK 10.0.100 が入っていればそのまま使います。`--delayacct` はスレッド別の I/O 待ち時間（`io_wait_ms`）の記録を再起動まで有効にします。`--check` は確認だけを行います。

## 3. 入力データ

データを指定しなければ、生成データ（既定 96 MiB、MOD の探索対象と同じフォルダ構成で、重複・壊れた XML・壊れた PNG を意図的に含む）を使います。生成データでは仕込んだ件数と索引化の結果を照合します。

実データは R2 から必要なものだけを取得します。キーはシェルの環境変数に入れ、リポジトリやファイルには書きません。

```bash
export R2_ENDPOINT=https://<アカウントID>.r2.cloudflarestorage.com
export AWS_ACCESS_KEY_ID=...  AWS_SECRET_ACCESS_KEY=...
python3 -I tools/benchmark/fetch-r2-dataset.py ~/mur-data --profile game --list   # 計画だけ表示
python3 -I tools/benchmark/fetch-r2-dataset.py ~/mur-data --profile game
```

| プロファイル | 内容 | 目安（2026-11-18 時点の R2） |
|---|---|---|
| `smoke` | Core の公式データ、Assets.zip、Workshop の MOD 12 個 | 3,752 ファイル、0.37 GiB |
| `game` | Core と全 DLC の公式データ、Assets.zip | 1,541 ファイル、0.63 GiB |
| `all` | `game` と Workshop の全 MOD（281 個） | 44,998 ファイル、1.83 GiB |

取得するのは MOD の探索対象（Defs・Patches・Languages・Textures・Sounds の対象拡張子）と About.xml、loadFolders.xml、Assets.zip だけです。DLL や言語 .tar は取りません。Windows と同じく大文字小文字を区別せずに読めるよう、これらのフォルダ名は正規の綴りにそろえます。再実行すると取得済みのファイルは飛ばします。プロファイルを変えたときは `--prune` で計画外のファイルを消します。取得元と一覧の SHA-256 は `~/mur-data/r2-dataset.json` に残ります。

## 4. 実行

```bash
tools/benchmark/run-gcp-benchmark.sh smoke                         # まず動作確認
tools/benchmark/run-gcp-benchmark.sh standard --data ~/mur-data    # 通常の測定
tools/benchmark/run-gcp-benchmark.sh stress --data ~/mur-data      # 32 ワーカーまで・長め
tools/benchmark/run-gcp-benchmark.sh soak --hours 4 --data ~/mur-data   # リーク・解放の長時間確認
```

SSH が切れても止まらないよう、長い実行は `tmux` か `nohup ... &` の中で始めます。

| モード | ワーカー数 | 試行（＋ウォームアップ） | 対象ファイル上限 | RAM 予算 | 時間上限 |
|---|---|---|---|---|---|
| smoke | 1,2,4 | 2（＋1） | 1,500 件 / 256 MiB | 256 MiB | 20 分 |
| standard | 1,2,4,8,16 | 5（＋1） | 20,000 件 / 4 GiB | 2 GiB | 6 時間 |
| stress | 1〜32 | 3（＋1） | 50,000 件 / 16 GiB | 8 GiB | 10 時間 |
| soak | 自動 | 段階を繰り返す | standard と同じ | 2 GiB | 指定時間＋15 分 |

主なオプション: `--cpus 4`（CPU 0〜3 に固定）、`--workers 1,2,4,8`、`--trials 7`、`--ram-budget-mib 4096`、`--max-files 10000`、`--max-mib 2048`、`--max-runtime-min 120`、`--scenarios audit,scan,prefetch`、`--tests`（先に単体テスト）、`--label 名前`。`--` の後ろはベンチマーク本体へそのまま渡します（`--help` で一覧）。

- GC 設定の比較: `--gc-variants default,server,conserve,concurrent-off` で同じ測定を GC 設定ごとに行い、最初の結果と比較します（`DOTNET_gcServer=1`、`DOTNET_GCConserveMemory=5`、`DOTNET_gcConcurrent=0`）。
- cold キャッシュ: 既定は対象ファイルだけを `posix_fadvise(DONTNEED)` で追い出し、`mincore` で追い出せたかを確かめます。`--root-cold` は sudo でページキャッシュ全体とメタデータを捨てる方法に切り替え、ファイル探索の「メタデータ cold」条件も測ります。root では権限障害（chmod）が読み取りを止めないため、その条件は集計から外れます。

所要時間の実測は、4 vCPU のコンテナで smoke（生成データ、ワーカー 1,2、試行 1、ウォームアップ 0）が約 3.5 分、既定の smoke を R2 の smoke プロファイル（1,500 ファイルを選択、パック 2 個）で実行スクリプトから流して 13 分 40 秒でした。standard と stress はまだ VM で流していません。条件数 × (試行＋ウォームアップ) × 1 試行の時間で決まり、Pump 間隔に縛られる条件は `--rate-window-s`（standard 20 秒）で打ち切るため、データ量よりも条件数の影響が大きくなります。

### 止め方と安全装置

Ctrl+C（SIGINT）か SIGTERM で、実行中の試行を終えてから結果と分析を書いて終了します。2 回目で即終了します。次のどれかを超えると自動で止まり、そこまでの結果は残ります: 時間上限、プロセス RSS（既定は物理メモリの 3/4）、マシンの空きメモリ、作業用ファイルの容量、ディスクの空き。

終了コード: 0 正常、1 エラー、2 引数の誤り、3 中断（結果は保存済み）、4 安全装置による停止、5 失敗した試行あり。

## 5. 結果の読み方

結果は `~/mur-bench-results/<UTC日時>-<コミット7桁>-<モード>[-ラベル]/` に入ります。

| ファイル | 内容 |
|---|---|
| `analysis.md` | 日本語の分析。最初に読む |
| `metadata.json` | コミット SHA・ブランチ・未コミット変更の有無、VM（GCE のマシンタイプ・ゾーン、CPU、メモリ、カーネル）、.NET と GC 設定、全オプション、入力データの指紋、開始日時 |
| `trials.csv` | 1 試行 1 行の主要値（下表） |
| `metrics.csv` | 全指標の縦持ち（`seq,metric,value`）。表計算やスクリプトでの集計用 |
| `trials.jsonl` | 試行の全情報（注記・エラー含む）。`analyze` と `compare` はこれを読む |
| `summary.json` | 条件ごとの中央値・P95・最小・最大・CV・IQR と、除外した試行の理由 |
| `soak-*.csv`, `soak-summary.json` | soak のみ。時系列、全 GC 後の定点値、段階の切り替え、リーク判定 |

`trials.csv` の主な列: `items_per_s`（ファイル/秒）、`mib_per_s`、`wall_ms`、`cpu_ms`・`cpu_parallelism`（CPU 時間 ÷ 経過時間）、`worker_busy_pct`（ワーカーがジョブを実行していた割合）、`rss_peak_mib`・`rss_anon_peak_mib`・`native_peak_mib`、`gc_gen0/1/2`・`gc_pause_ms`・`gc_pause_max_ms`・`alloc_mib_per_s`、`io_storage_read_mib`（ストレージから実際に読んだ量）・`io_read_mib`（read の合計）・`io_wait_ms`・`psi_io_some_ms`、`sys_steal_pct`、`run_queue_wait_pct`、`lock_contentions_per_s`、`errors`、`leaked_mod_threads`・`leaked_open_files`（試行後に残ったスレッドとファイル）。

判定の規則:

- 各値は集計対象の試行の中央値です。P95 は試行が 20 未満だと最大値に近いので参考値として扱います。CV（変動係数）が 10% を超える条件は、試行を増やすか VM を替えて測り直します。
- 集計対象にするのは、正常に終わり、ウォームアップでなく、要求したキャッシュ状態を `mincore` で確認できた試行だけです。cold を再現できなかった試行は理由付きで除外し、warm と混ぜません。
- 時間上限で打ち切った試行（`truncated=1`）は速度だけを使い、完了時間は集計しません。
- 「ワーカーを増やしても伸びない」は、より多いワーカー数が中央値で +10% を超え、かつ全試行の範囲が重ならない場合だけを伸びたとみなして判定します。
- `variant=cadence` はコントローラと同じ 250 ms ごと、`variant=saturated` はワーカーが待ち行列のジョブを取り終えるたびに Pump を呼びます（プールの待ち行列に Pump 用のジョブを入れて回します）。saturated は作業自体の処理能力で、ゲーム内の速度ではありません。
- `律速要因` 列は各条件の計測値から機械的に付けます（Pump 間隔、CPU、CPU 競合、I/O 待ち、vCPU 超過、ロック競合、GC 停止、供給側）。ロック競合はプロセス全体の数なので、効率が 0.85 を下回った条件でだけ律速要因に挙げます。基準は設計書の「統計と判定」にあります。
- 「データの有効性」に JIT の混入（JIT の時間が経過時間の 5% 以上の試行数）が出たら、その条件の速度は低めです。ウォームアップを増やす（`--warmup 2`）と減ります。
- vCPU 数を超えるワーカー数は `*` 付きで、そのマシン上での過剰割当の結果です。より多いコアのマシンの性能を表しません。

2 回の結果の比較:

```bash
tools/benchmark/run-gcp-benchmark.sh compare ~/mur-bench-results/<A> ~/mur-bench-results/<B>
```

`comparison.md` は、両方 3 試行以上あり、中央値の差が 3% 以上で、全試行の範囲が重ならないものだけを「改善」「悪化」とし、それ以外は「差なし」「試行不足」とします。入力データの構成指紋が違う場合は比較しません。マシン・カーネル・.NET・GC 設定が違う場合は警告を出します。

結果を手元へ持ち帰る例:

```bash
gcloud compute scp --recurse mur-bench:~/mur-bench-results ./ --zone=asia-northeast1-b
```

## 6. このベンチマークで再現できないこと

- ゲーム本体（メインスレッド、フレーム時間、GPU、テクスチャ転送）。FPS・TPS・ロード時間の改善は主張できません。実機での確認は [Windows 実機測定手順](windows-waylith-benchmark-ja.md) で行います。
- Windows の I/O とメモリ管理（NTFS、スタンバイリスト、ウイルス対策ソフト、Windows ヒープの解放後の挙動）。ゲームは Windows 版 1.7.5011（WaylithCLR 10.0.15）で、ここは Ubuntu の .NET ランタイムです。
- ワーカーの優先度: MOD は Lowest を指定しますが、Linux の一般ユーザーでは効果がありません。
- WOWGAG's Performance Patch、Axen's Performance Enhancements、Overclock の実際の並列処理。ここでは「稼働中」という通知だけを与え、MOD がワーカー上限を下げないことを確かめます。
- 本物のメモリ枯渇（ページアウト・OOM）とディスク自体の読み取りエラー。メモリ不足は MOD が読む空きメモリの値だけを下げて再現します。
- 1.7 用フォルダを持たない旧バージョン向け MOD の読み込みフォルダ。ここではルートと Common だけを使い、該当 MOD の数を `analysis.md` に出します（ゲームが旧フォルダに代替するかは未確認）。
