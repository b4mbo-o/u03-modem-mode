# Changelog

## Unreleased

- Add a standalone C# Windows Forms EXE with status, modem, rollback, and log-copy controls.
- Embed automatic UAC elevation in the EXE; no adjacent PowerShell files are required.
- Build and verify the Windows EXE in GitHub Actions and attach it to tagged releases.
- Add a Windows PowerShell 5.1 CLI as a troubleshooting fallback.
- Support status detection and best-effort modem-to-RNDIS rollback through a ZTE COM port.
- Bind Windows Web UI requests to the U03 RNDIS address to avoid same-subnet misrouting.
- Document the Windows driver requirement and the unsupported native `1484` USB bulk transition.

## 0.3.0 - 2026-09-15

- Add `u03-sms-receive` to configure and read SMS in modem mode.
- Work around the U03 internal-NV receive failure by selecting SIM-first SMS storage.
- Prefer packet-switched SMS and use store-and-notify routing compatible with the U03.
- Decode GSM 7-bit and UCS-2 SMS-DELIVER PDUs without third-party Python packages.
- Keep messages on the SIM and optionally append them to a private JSONL inbox.

## 0.2.0 - 2026-09-15

- Add `--to-rndis` to restore the persistent RNDIS/Web UI mode from `19d2:1481`.
- Recover devices left in the `19d2:0016` factory/diagnostic composition.
- Validate each AT command response before rebooting.
- Send the Web UI's expected `Host` header so modem switching also works after a rollback.
- Document the reverse-engineered `ZCPE`, `ZCDRUN`, and `ZRST` rollback sequence.

## 0.1.0 - 2026-09-14

- Initial Linux release for the au ZTE Speed USB STICK U03/MF871.
- Support `19d2:1484` virtual CD-ROM to `19d2:1483` RNDIS switching.
- Enable persistent `19d2:1481` modem mode through the device Web API.
- Isolate device traffic in a temporary network namespace.
- Detect the resulting AT and MODEM/PPP serial ports.
