# WT-131 Manual Test Harness

## 1. Required Assumptions

- Virtual audio driver/cable is installed and visible to OS.
- Electron app exposes device selection for all required roles.
- MeetingService + transcript pipeline are running.

## 2. Preflight Checklist

1. Confirm services are reachable and healthy.
2. Confirm four device roles are bound correctly:
- Real microphone input
- Virtual speaker/cable capture input
- Virtual microphone output to meeting app
- Real speaker/headphones output
3. Confirm OS permissions for microphone/system audio capture.
4. Confirm transcript panel is visible and receiving updates.

## 3. Direction Test Cases

## TC-01 Local -> Remote (Vietnamese -> English)

- Step: User A speaks scripted Vietnamese sentence.
- Expected:
- Remote side hears translated English output.
- Transcript shows Vietnamese source and English translated segment.

## TC-02 Remote -> Local (English -> Vietnamese)

- Step: Remote participant speaks scripted English sentence.
- Expected:
- User A hears translated Vietnamese output.
- Transcript shows English source and Vietnamese translated segment.

## 4. Failure-Case Coverage

## FC-01 Missing virtual speaker/cable device

- Inject: Disable/unplug virtual speaker device.
- Expected: Harness marks failure category `device_missing`.
- Recovery:
1. Re-enable/rebind device.
2. Rerun preflight checks.
3. Repeat TC-02.

## FC-02 Permission denied

- Inject: Revoke microphone/system capture permission.
- Expected: Harness marks failure category `permission_denied`.
- Recovery:
1. Re-grant permission.
2. Restart capture pipeline.
3. Repeat TC-01.

## FC-03 High latency / degraded pipeline

- Inject: Simulate constrained network/slow processing path.
- Expected: Harness marks failure category `pipeline_degraded`.
- Recovery:
1. Switch to short scripted utterances.
2. Continue transcript-first verification if TTS delay is high.
3. Record degraded status and completion notes.

## 5. Evidence Capture Template

For each test case, capture:
- Timestamp
- Device mapping snapshot
- Result: pass/fail
- Observed transcript behavior
- Observed audio behavior
- Failure category (if fail)
- Recovery action and final status
