# Protocol notes

この文書は、au ZTE U03/MF871で確認したモード切替プロトコルの技術メモです。

## USB compositions

- `19d2:1484`: USB Mass Storage（仮想CD-ROM）
- `19d2:1483`: RNDISとWeb UI
- `19d2:1481`: ATポートとMODEM/PPPポート
- `19d2:0016`: factory/diagnostic構成。本ツールでは操作しない

`1484`から`1483`への切替には、次のUSB Mass Storageメッセージを順に使用します。

```text
5553424312345678000000000000061b000000020000000000000000000000
55534243876543212000000080000c85010101180101010101000000000000
```

## Web API

RNDIS構成の管理アドレスは`192.168.100.1`です。以下のヘッダーがないリクエストでは値が空になるファームウェアがあります。

```http
Referer: http://speedusb-stick.home/index.html
X-Requested-With: XMLHttpRequest
```

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

## 出典と実機確認

`SET_PRODUCT_MODE_FOR_KDDI`は、ZTEが配布しているA002ZT向けModem switch tool内の設定文字列を手掛かりにしました。`RD`/`AD`の生成方法は端末Web UIのJavaScriptと実機応答から確認しています。

`19d2:1481`のインターフェース0がAT、インターフェース1がMODEMであることは、LinuxカーネルのU03/MF871A対応パッチおよび実機の両方で確認しました。

このリポジトリには、調査対象となったプロプライエタリな実行ファイル、JavaScript、ファームウェアを含めません。
