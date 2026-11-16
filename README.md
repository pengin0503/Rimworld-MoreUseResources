# Rimworld-MoreUseResources

RimWorld 1.7で、CPUワーカー・RAM・VRAMを動作フェーズごとに使うMODです。基準は **Windows 1.7.5011 rev24 / .NET 10 / Waylith** です。

## 導入

1. RimWorld 1.7対応の **Harmony 3** を導入し、本MODの前にロードします。
2. ZIPを解凍し、`Rimworld-MoreUseResources` フォルダをゲームの `Mods` に配置します。中に `About/About.xml` と `Assemblies/MoreUseResources.dll` があれば正しい構成です。GitHubのソースZIPを展開して使う場合も同じ構成です。
3. MODを有効にして再起動し、**オプション → MOD設定 → More Use Resources** を開きます。設定画面は日本語・英語に対応します。

## 動作と設定

| リソース | 実行する処理 | 初期値 |
| --- | --- | --- |
| CPU | ワーカーで有効なMODと公式コンテンツのアセットを読み、OSのファイルキャッシュへ先読みします。ワーカー上限と実験的なCPU負荷生成の稼働率を設定できます。 | 最大4ワーカー、CPU負荷生成OFF |
| RAM | ファイルコピーの保持とRAMの追加予約を個別に調整できます。OSの空き容量を監視し、圧迫時は予約から先に解放します。 | OS先読みON、プロセス内コピーと追加予約OFF、使用可能RAMの下限6 GiB |
| GPU | Waylith経由で、未常駐かつ再読込可能な小型テクスチャを先読みします。ドライバー予算に対する上限、残す容量、処理時間の目安を設定できます。 | 実験機能OFF |

- CPU・RAMは起動後、タイトル画面、ロード・マップ生成、通常速度、高速、ゲームのポーズ、セーブ・オートセーブで個別に切り替えられます。高速は通常より上のゲーム速度です。初期状態ではセーブ中に新規作業を抑え、ウィンドウが非アクティブな間は休止します。
- CPU設定は並列ワーカーと追加負荷に適用します。RAM設定だけでファイルコピーを有効にした場合も、読み取り用のワーカーを1つ使います。RAMの実行枠がOFFのときは、CPUワーカーがOS先読みだけを行います。
- 「今すぐ実行」は手動で強度100%にします。セーブ、非アクティブ時、ワールド画面の設定、メモリ予算は引き続き適用されます。「RAM解放」はMODが保持するコピーと予約を解放して一時停止します。「自動」でフェーズ制御に戻ります。
- 状態欄にフェーズ、ワーカー枠、先読みの進捗、コピー・予約した容量、プロセスのワーキングセット、GPUの最終予算サンプルを表示します。

## 実装範囲

- 先読み対象は有効なMODの `Defs`、`Patches`、`Languages`、`Textures`、`Sounds` 内の対象ファイルです。読み取り専用で、セーブ・設定ファイルやゲームのシミュレーション処理には介入しません。設定はRimWorldのMOD設定ファイルに保存されます。
- ゲームが再利用できるのはOSのファイルキャッシュです。プロセス内コピー、RAM予約、CPU負荷生成は使用量を増やすための任意機能です。FPS・TPS・ロード時間の改善は計測していません。
- GPU先読みは **Windows 1.7.5011 rev24** の単体GPUで、Waylithのテクスチャ用デバイスローカルヒープとVulkanドライバー予算を識別できる場合に、タイトル・通常速度・高速・ポーズの安定したフレームで動きます。起動・ロード・セーブ中にはGPU処理を開始しません。
- GPU処理は1回1テクスチャで、遅いアップロードの後は間隔を広げます。単一のアップロードは指定時間を超える場合があります。常駐した画像はWaylithが管理します。VRAMの人工予約、テクスチャの強制退避、GPUの合成負荷は提供していません。
- WindowsのメモリAPIとLinuxの `/proc/meminfo` をサポートします。OSから使用可能メモリを取得できない環境ではRAM追加確保と先読みを停止します。

## ビルドと検証

.NET SDK 10.0.100、NuGet、Python 3を使用します。ゲーム用DLLはRimWorld 1.7.5011の参照パッケージでビルドします。ZIPにはMOD自身のDLLとデータだけが入ります。

```sh
dotnet test Tests/MoreUseResources.Tests.csproj -c Release
dotnet build Source/Game/MoreUseResources.csproj -c Release
cp Source/Game/bin/Release/net10.0/MoreUseResources.dll Assemblies/
python3 tools/package_mod.py dist/Rimworld-MoreUseResources.zip
```

Windows 1.7.5011 rev24の実DLLで、フレーム更新、セーブ、Waylithのテクスチャ再アップロードとVRAM予算取得の実装を静的に確認しました。実ゲームでのMOD起動・設定画面の操作、セーブ／オートセーブ／再ロード、GPU実機での使用量とフレーム時間は別途確認が必要です。

[当初の設計資料](docs/plans/2026-11-16-resource-usage-and-activation-ja.md)
