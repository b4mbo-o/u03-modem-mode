# Protocol notes

この文書は、au ZTE U03/MF871で確認したモード切替プロトコルの技術メモです。

## USB compositions

- `19d2:1484`: USB Mass Storage（仮想CD-ROM）
- `19d2:1483`: RNDISとWeb UI
- `19d2:1481`: ATポートとMODEM/PPPポート
- `19d2:0016`: factory/diagnostic構成。`--to-rndis`での復旧入力として対応

`1484`から`1483`への切替には、次のUSB Mass Storageメッセージを順に使用します。

```text
5553424312345678000000000000061b000000020000000000000000000000
55534243876543212000000080000c85010101180101010101000000000000
```

## Web API

RNDIS構成の管理アドレスは`192.168.100.1`です。以下のヘッダーがないリクエストでは値が空になるファームウェアがあります。

```http
Host: speedusb-stick.home
Referer: http://speedusb-stick.home/index.html
X-Requested-With: XMLHttpRequest
```

特にRNDISへ復帰した直後は、`Host`がIPアドレスのままだと`RD`が空文字になることを実機で確認しています。

認証用ランダム値は次のGET APIで取得します。

```http
GET /goform/goform_get_cmd_process?isTest=false&cmd=RD&multi_data=1
```

返された32桁の`RD`から`AD`を生成します。

```text
AD = MD5("94CB8A5309AF41BDBFC855A9BF2A3A5E" + RD)
```

永続的なKDDIモデム製品モードを設定するPOSTデータは次の通りです。

```text
isTest=false
goformId=SET_PRODUCT_MODE_FOR_KDDI
debug_enable=1
AD=<computed value>
```

送信先：

```text
/goform/goform_set_cmd_process
```

成功時は`{"result":"success"}`が返ります。続けて新しい`RD`を取得し、同じ方法で`AD`を生成して再起動を要求します。

```text
isTest=false
goformId=REBOOT_DEVICE
AD=<new computed value>
```

再起動中にHTTP接続が切れるのは正常です。USBが`19d2:1481`として再列挙されれば切替成功です。

## ATコマンドによるRNDIS復帰

`1481`のインターフェース0（通常は`/dev/ttyUSB0`）で、次のコマンドを1つずつ送り、各コマンドの`OK`を待ちます。

```text
AT+ZCPE=o
AT+ZCDRUN=8
AT+ZCDRUN=F
AT+ZRST
```

それぞれの役割は次の通りです。

- `AT+ZCPE=o`: CPE/KDDIモデム製品モードを解除する
- `AT+ZCDRUN=8`: 仮想CD-ROMのオートランを無効化する
- `AT+ZCDRUN=F`: download/factoryモードを終了する
- `AT+ZRST`: 端末を再起動する

`ZCPE`、`ZCDRUN=8`、`ZCDRUN=F`では、本文中に`SUCCESS):1`があり、最後に`OK`が返ることも検証します。再起動後に`1484`で現れた場合は、前述のMass Storageメッセージで`1483`へ切り替えます。

診断用`0016`から復旧する場合はインターフェース2（通常は`/dev/ttyUSB2`）を使用し、同じシーケンスを送ります。この経路はAPN等を消す工場出荷時リセットではありません。

## SMS受信の回避設定

U03実機では、内蔵NV優先の初期値`AT+ZSMSD=2`のままでは、LTE登録・データ接続・`CNMI`設定が正常でも着信SMSがATポートにも内蔵NVにも現れませんでした。次の設定でSIMへ保存する経路に切り替えると、NTTドコモ網の日本通信SIMで保留分を含むSMS受信を確認できました。

```text
AT+CMGF=0
AT+CGSMS=2
AT+ZSMSD=1
AT+CPMS="SM","SM","SM"
AT+CNMI=1,1,0,0,0
```

- `CMGF=0`: PDUモード
- `CGSMS=2`: packet-switched bearer優先
- `ZSMSD=1`: (U)SIMをメッセージ保存先として優先
- `CPMS="SM"`: 読み書き対象をSIMに設定
- `CNMI=1,1,0,0,0`: 受信SMSをSIMへ保存し、可能ならインデックスを通知

`CGSMS=2`と直接転送設定だけでは受信せず、`ZSMSD=1`への変更後にSIM使用件数が0件から4件へ増加したため、内蔵NV保存経路が主因と判断しています。`u03-sms-receive`は通知だけに依存せず、`AT+CMGL=4`も定期実行します。

この設定はSMS用であり、APNやPPP接続を構成しません。実機ではMODEMポートのPPP接続によってLTE登録状態になった後に受信しました。SIMのSMS容量は20件で、本ツールはデータ保護のため削除コマンドを送りません。

## 出典と実機確認

`SET_PRODUCT_MODE_FOR_KDDI`は、ZTEが配布しているA002ZT向けModem switch tool内の設定文字列を手掛かりにしました。`RD`/`AD`の生成方法は端末Web UIのJavaScriptと実機応答から確認しています。

RNDIS復帰の`AT+ZCPE=o`と`AT+ZRST`も同ツールのコマンド表と通常モード切替処理から特定し、U03実機の応答と`1483`への再列挙で確認しました。一般的なZTE端末で案内される`AT+ZCDRUN=8/9/F`だけでは、U03のKDDI製品モードフラグは解除されません。

`19d2:1481`のインターフェース0がAT、インターフェース1がMODEMであることは、LinuxカーネルのU03/MF871A対応パッチで確認できます。実機では両方がATコマンドへ応答したため、SMSツールは使用中でないAT応答ポートを自動選択します。

このリポジトリには、調査対象となったプロプライエタリな実行ファイル、JavaScript、ファームウェアを含めません。
