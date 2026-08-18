# Report Old Version Excel Template Check

Date: 2026-08-15
Scope: Read-only check of Excel files under `Report - old version`.

## Key Decision

Keep the original Excel templates. Do not delete `Guideline`, example/template sheets, or official control sheets when fixing Report5. Use the old-version workbooks as the layout/style source of truth.

## Files Checked

| File | Role | Sheets | Notes |
| --- | --- | ---: | --- |
| `Report - old version/Report5_Unit Test.xlsx` | Original Unit Test template | 8 | Primary template for Unit workbook |
| `Report - old version/Report5_Test Report.xlsx` | Original Test Report template | 5 | Primary template for System/API report workbook |
| `Report - old version/Report3_Project Tracking.xlsx` | Project tracking template/data | 5 | Has WBS, Issues, Defects, Q&A tracking sheets |
| `Report - old version/References.xlsx` | Reference research table | 2 | Research and feature reference data |
| `Report - old version/Project Weekly Report_GroupName.xlsx` | Weekly report template | 1 | Simple weekly report template |

## Unit Test Template Findings

Source workbook: `Report - old version/Report5_Unit Test.xlsx`

Template sheets:

- `Guideline`
- `Cover`
- `Functions`
- `Statistics`
- `Function 1`
- `Function 2`
- `Function3`
- `Example`

Important layout pattern:

- Function template sheets use metadata rows `2:7`.
- UTCIDs begin on row `9`.
- Test matrix starts around row `10`.
- Function sheets use freeze pane `A10`.
- Original formulas compute:
  - Passed: `COUNTIF(...,"P")`
  - Failed: `COUNTIF(...,"F")`
  - Untested: derived from total minus passed/failed
  - N/A/B: counted from UTCID classification row
  - Total Test Cases: `COUNTA(...)`

Current workbook observations:

- `My Drive/Report5/Report5_Unit Test.xlsx` still keeps `Guideline`, which is good and should remain.
- Current function sheets are expanded to `F001-F035`, but some no longer preserve the exact template formula-driven structure.
- `F007_CreateWorkspace` is visibly shifted: row 2 begins with `Created By` instead of `Function Code`, UTCID row is shifted from row 9 to row 8, and the test requirement text is wrong/misaligned.
- Several current function sheets have manual/static totals or blank total cells where the template intended formulas.
- Current function sheets have more merged ranges than old template sheets; repairs should match the intended template layout without broad restyling.

## Test Report Template Findings

Source workbook: `Report - old version/Report5_Test Report.xlsx`

Template sheets:

- `Cover`
- `Test Cases`
- `Test Statistics`
- `Feature 1`
- `Feature 2`

Important layout pattern:

- Feature template sheets use metadata rows `2:8`.
- Test case headers are on row `10`.
- Function group label is on row `11`.
- Test cases start at row `12`.
- Feature sheets use freeze pane `A11`.
- Round result columns:
  - Round 1: column `F`
  - Round 2: column `I`
  - Round 3: column `L`

Formula issue inherited from template:

- Old `Feature 1` and `Feature 2` count Round 1, Round 2, and Round 3 from column `F`.
- Current workbook partially fixes this by counting Round 2 passed from `I` and Round 3 passed from `L`, but failed/pending/N/A still point to column `F`.
- Correct fix: preserve row/column layout, but update Round 2 formulas to use `I` and Round 3 formulas to use `L` for every status.

Current workbook observations:

- `My Drive/Report5/Report5_Test Report.xlsx` expands feature sheets to 35 WarpTalk sheets.
- The feature sheet layout mostly preserves the original `Feature 1/2` template.
- `Test Statistics` still contains stale non-WarpTalk module labels from an unrelated template and must be cleaned while preserving table layout.
- `Traceability Matrix` is an added evidence sheet; keep it if needed, but do not use it as a reason to delete original template/control sheets.

## Other Old-Version Workbooks

### `Report3_Project Tracking.xlsx`

Sheets:

- `WBS mẫu`
- `WBS`
- `Issues`
- `Defects`
- `Q&A`

Notes:

- This is a tracking workbook, not a Report5 test template.
- Keep `WBS mẫu` as the template/sample sheet.
- Current data sheets include open/pending placeholder statuses and should be reviewed separately if used in final submission.

### `References.xlsx`

Sheets:

- `Research Table`
- `Tab Feature`

Notes:

- This contains project research and feature-reference data.
- No formula errors found in the quick scan.

### `Project Weekly Report_GroupName.xlsx`

Sheets:

- `Wx`

Notes:

- Simple weekly report template.
- Keep placeholders unless generating an actual weekly report instance.

## Fix Rules Going Forward

1. Preserve original template sheets unless the user explicitly approves hiding or removing them.
2. Use old-version workbooks as layout/style references before editing current Report5 files.
3. Repair formulas and content inside the existing template layout.
4. Add evidence columns/sheets only when they do not break required template structure.
5. If extra traceability sheets are added, keep them additive and clearly named.
6. Do not restyle whole workbooks or autofit all sheets blindly.
