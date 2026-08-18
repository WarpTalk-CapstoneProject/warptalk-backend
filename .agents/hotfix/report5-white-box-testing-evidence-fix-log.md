# Report5 White-Box Testing Evidence Fix Log

Updated: 2026-08-15 18:31:11 +07:00

## Files changed

- `My Drive/Report5/Report5_Unit Test.xlsx`
- `My Drive/Report5/Report5_Test Report.xlsx`

## Changes applied

- Preserved original workbook sheet structure:
  - Unit workbook: 40 sheets.
  - Test Report workbook: 39 sheets.
- Repaired `Report5_Unit Test.xlsx`:
  - Fixed `F007_CreateWorkspace` metadata/header alignment and `Statistics` references.
  - Restored dynamic summary formulas on all 35 function sheets using `COUNTIF`, `SUM`, and `COUNTA`.
  - Recomputed the previously inconsistent `N/A/B` evidence through formulas for `F008`, `F010`, `F012`, `F013`, `F019`, `F020`, and `F021`.
  - Restored function-sheet freeze panes to `A10`, matching the old template function sheets.
- Repaired `Report5_Test Report.xlsx`:
  - Removed stale template labels from `Test Statistics`.
  - Fixed Round 2 formulas to count result column `I`.
  - Fixed Round 3 formulas to count result column `L`.
  - Added Postman/API evidence wording in existing template locations only:
    - `Test Cases!D5`
    - `Traceability Matrix!H`
  - Reconciled the actual committed Postman artifacts:
    - `warptalk-backend/postman/WarpTalk-Backend.postman_collection.json`
    - `warptalk-backend/postman/environments/WarpTalk-Backend.Local.postman_environment.json`
    - Collection scope: 245 requests across Auth, Workspace, Billing, Meeting, Translation Room, Transcript, Notification, Assistant, and Gateway/Platform.
    - No Newman run log or `pm.test` assertion script claim is made.
  - Added UAT trace rows `UAT-UC-01` through `UAT-UC-08` using actual use-case flows and Linear/WT bug-ticket regression references.

## Template constraints

- Did not add new sheets.
- Did not add new columns.
- Did not create an `Integration Evidence` sheet because that would change the original workbook architecture.
- Did not edit `Report5_Test Documentation.docx`; it remained locked by Word/Drive (`~$port5_Test Documentation.docx` present) and no Postman/Newman claim was found in the DOCX text scan.

## Verification

- Formula-error scan:
  - `Report5_Unit Test.xlsx`: no `#REF!`, `#DIV/0!`, `#VALUE!`, or `#NAME?`.
  - `Report5_Test Report.xlsx`: no `#REF!`, `#DIV/0!`, `#VALUE!`, or `#NAME?`.
- Stale-label scan:
  - No old venue/advertisement/voucher/personality/mood labels found in either workbook.
- Postman scan:
  - Root `test` and `tests` folders do not exist.
  - Current Postman artifacts exist under `warptalk-backend/postman`.
  - Historical root script `fix.js` references `warptalk-backend/test/postman/notification/notification.postman_collection.json`, but that old path is absent in the current checkout.
  - `Report5_Test Report.xlsx` now points to the current `warptalk-backend/postman` location.

## Hashes after fix

- `Report5_Unit Test.xlsx`: `BC0CD523C10AF6490E6365E93249B0D12E9A5345C431D1A3E8E7271D55E8921B`
- `Report5_Test Report.xlsx`: `C825D045020523A7F2456A33682AC5B4A9FF668F3C900A101C97DE90517195FE`

## Drive sync verification

Remote checked with `rclone lsjson warptalk_drive_folder:Report5`.

- `Report5_Unit Test.xlsx`
  - Remote size: `1937640`
  - Remote mod time: `2026-08-15T11:27:34.342Z`
  - Remote ID: `1C1z-0wYYcee0azaZsjcXZc1Ec6JKAmJe`
- `Report5_Test Report.xlsx`
  - Remote size: `2079356`
  - Remote mod time: `2026-08-15T11:38:45.842Z`
  - Remote ID: `1mpOZcfzGMzNFq05F9BZSc5uNFCcs0Lpe`
