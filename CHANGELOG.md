# Changelog

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
