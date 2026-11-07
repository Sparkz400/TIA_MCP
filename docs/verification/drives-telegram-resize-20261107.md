# Drive telegram resize, additional and torque telegrams — 7 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.drives.telegram-resize.20261107`](../../evidence/local.drives.telegram-resize.20261107.json) | `qualify_drives.py` | 44 steps |

`drive_telegram_set` gains `operation: resize` (one channel, `direction` input or output, `size` in PROFIdrive words,
`keepOriginalAddress`) for the main, supplementary or additional telegram, after Startdrive's `CanChangeSize`, and inserts
or erases a torque telegram by number. On the G120C PN of the drive runner (offline project, no IO system):

- the main telegram 1's input channel went from 2 to 4 words (4 to 8 bytes), output unchanged, and restored from its
  checkpoint;
- an additional telegram was inserted with 2/2 words (Startdrive numbers it 999, 4/4 bytes); its input channel was resized
  to 4 words (8 bytes) with `keepOriginalAddress`, restored, and the telegram erased, which brought back the original rows;
- a resize without `keepOriginalAddress` and one in an unknown direction were refused (`InvalidRequest`);
- Startdrive refused torque telegram 750 on this drive (`TelegramChangeRefused`, nothing written).

## Defects found on the way

- The first two attempts (`dt1-r01`, `-r02`) inserted the additional telegram correctly, but the readback re-enumerated the
  telegram composition taken before the insert, which does not list the new telegram, and failed after the write. The
  worker now reads the drive object's telegrams afresh. This also affected the existing additional insert and erase, which
  had no native run before.
- Sizes are compared in bytes, 2 per word, as the main resize showed; the native `GetSize` values are retained beside them.

PKW has no Openness setter (`Telegram.PKW` is read-only); it follows the telegram number, which `change` already sets.

Not covered: start addresses (the drive is on no IO system, so `keepOriginalAddress` is not observable), a drive that
accepts a torque telegram, supplementary telegrams.
