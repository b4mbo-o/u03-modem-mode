# au ZTE U03 Modem Mode Switch for Linux

[![shellcheck](https://github.com/b4mbo-o/u03-modem-mode/actions/workflows/shellcheck.yml/badge.svg)](https://github.com/b4mbo-o/u03-modem-mode/actions/workflows/shellcheck.yml)
[![release](https://img.shields.io/github/v/release/b4mbo-o/u03-modem-mode)](https://github.com/b4mbo-o/u03-modem-mode/releases)

auのUSBデータ通信端末 **Speed USB STICK U03（ZTE MF871）** を、RNDISモードとUSBモデムモードの間で永続的に切り替えるLinux用ツールです。元のRNDIS/Web UIモードへの復帰と、モデムモードでの実験的なSMS受信にも対応します。

> [!WARNING]
> 端末内に保存されるUSB構成を変更します。au U03/MF871の実機1台で確認していますが、すべてのファームウェアを保証するものではありません。実行は自己責任でお願いします。

## 確認済みの状態遷移

| USB ID | 状態 |
|---|---|
| `19d2:1484` | 仮想CD-ROM |
| `19d2:1483` | RNDIS/Web UI |
| `19d2:1481` | モデム（AT + MODEM/PPP） |
| `19d2:0016` | 診断／復旧用 |

切替後は通常、次のポートが現れます。

| ポート | 用途 |
|---|---|
| `/dev/ttyUSB0` | ATコマンド |
| `/dev/ttyUSB1` | MODEM/PPP |

## 必要なもの

- Linux（root権限とnetwork namespaceが必要）
- Bash 4以降
- Python 3（SMS受信機能を使う場合）
- `curl`、`iproute2`、`coreutils`
- CD-ROM状態から開始する場合は`usb-modeswitch`
- `19d2:1481`対応のLinux `option` USBシリアルドライバー

Debian/Ubuntu系では、依存パッケージを次のように導入できます。

```console
sudo apt install curl iproute2 usb-modeswitch usbutils
```

## 使い方

リポジトリを取得します。

```console
git clone https://github.com/b4mbo-o/u03-modem-mode.git
cd u03-modem-mode
```

まず状態だけを確認します。この操作は端末を変更しません。

```console
./u03-modem-switch --status
```

モデムモードへ切り替える場合（`--to-modem`は省略可能）：

```console
sudo ./u03-modem-switch --to-modem
```

確認を省略する場合：

```console
sudo ./u03-modem-switch --yes
```

成功時はUSB IDが`19d2:1481`になります。

元のRNDIS/Web UIモードへ戻す場合：

```console
sudo ./u03-modem-switch --to-rndis
```

成功時はUSB IDが`19d2:1483`になります。確認を省略する場合は同様に`--yes`を追加できます。復帰処理は工場出荷時リセットではないため、APN設定は消去しません。

```console
lsusb -d 19d2:
ls -l /dev/ttyUSB*
```

ATポートの動作例：

```console
sudo picocom -b 115200 /dev/ttyUSB0
```

接続後に`AT`を入力し、`OK`が返れば認識できています。

## SMSを受信する

`19d2:1481`のモデムモードで、APNを設定してMODEM/PPPポートから先に回線を接続します。`u03-sms-receive`自身はAPN設定やデータ接続を行いません。

空いているATポートからSMSを受信します。

```console
sudo ./u03-sms-receive
```

本ツールはAT応答があり、ほかのプロセスが使用していないU03のポートを自動選択します。明示する場合は次のように指定できます。

```console
sudo ./u03-sms-receive --port /dev/ttyUSB0
```

設定だけを適用する場合：

```console
sudo ./u03-sms-receive --setup-only
```

現在SIMにあるSMSを一度だけJSONで表示する場合：

```console
sudo ./u03-sms-receive --once --json
```

新しく見つけたSMSをパーミッション`0600`のJSON Linesファイルへ追記する場合：

```console
sudo ./u03-sms-receive --json --inbox ./u03-inbox.jsonl
```

U03実機では内蔵NV優先の初期設定だと着信SMSが保存されませんでした。本ツールはPS優先、SIM優先、store-and-notifyへ設定してこの問題を回避します。NTTドコモ網の日本通信SIMで、LTE接続中の日本語SMS受信を確認しています。

SMSはSIMから自動削除しません。実機のSIM保存容量は20件だったため、満杯になる前に別途整理してください。送信元番号と本文は通常の出力および`--inbox`へ含まれるので、ファイルの取り扱いにも注意してください。

## 同じサブネットを使っている環境について

U03のWeb UIは`192.168.100.1`を使います。PC側LANも`192.168.100.0/24`の場合、通常のcurlでは別の機器へ誤接続する可能性があります。

本ツールはU03のRNDISインターフェースを一時的なnetwork namespaceに隔離して通信します。既存のデフォルトルート、DHCP、`/etc/resolv.conf`は変更しません。

## 仕組み

1. 必要ならUSB Mass Storageメッセージで`1484`から`1483`へ切り替える
2. U03のWeb APIから`RD`トークンを取得する
3. 端末Web UIと同じ方法で`AD`を生成する
4. KDDI向け製品モード設定`SET_PRODUCT_MODE_FOR_KDDI`を送る
5. 新しいトークンで再起動し、`1481`への再列挙を確認する

RNDISへ戻す場合は、`1481`のATポートからCPE/KDDI製品モードを解除し、仮想CD-ROMと診断モードを無効化して再起動します。途中で`1484`になった場合は、USB Mass Storageメッセージで`1483`まで進めます。診断用`0016`に入ってしまった端末も`--to-rndis`で復旧できます。

詳細は[docs/protocol.md](docs/protocol.md)にまとめています。

## トラブルシューティング

### `no supported ZTE U03 USB ID found`

`lsusb -d 19d2:`でUSB IDを確認してください。本ツールは誤操作防止のため、`1484`、`1483`、`1481`、診断用`0016`以外には触れません。

### `could not find the U03 RNDIS interface`

RNDISドライバーを確認し、必要ならインターフェースを明示します。

```console
ip link
sudo ./u03-modem-switch --interface enx0123456789ab
```

### `1481`だが`ttyUSB`が出ない

```console
sudo modprobe option
dmesg | tail -50
```

古いカーネルにはU03のUSB ID定義がない場合があります。下記のLinuxカーネルパッチを参照してください。

### 元に戻したい

次を実行してください。

```console
sudo ./u03-modem-switch --to-rndis
```

`could not find the U03 AT port`と表示された場合は、`sudo modprobe option`を実行してから再試行してください。ModemManagerなどがATポートを使用中の場合は、その接続を切ってから実行します。

## 調査資料

- [ZTE公式 A002ZT ソフトウェアダウンロード](https://www.ztedevices.com/jp/?soft=a002zt-tool-download)
- [Linux kernel patch: Support ZTE MF871A USB modem](https://lists.openwall.net/linux-kernel/2019/08/22/1121)
- [au U03 取扱説明書（PDF）](https://www.au.com/content/dam/au-com/static/designs/extlib/pdf/support/mobile/guide/manual/business/download/u03_torisetsu.pdf)

## ライセンス

[MIT License](LICENSE)。製品名および商標は各所有者に帰属します。本プロジェクトはKDDI株式会社およびZTE Corporationの公式プロジェクトではありません。
