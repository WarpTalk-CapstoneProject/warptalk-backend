# Quickstart: WT-132 Third-Party Meeting Demo Flow

## 1. Demo Baseline

- Platform: Zoom
- Direction A: User A (Vietnamese) -> Remote participant hears English
- Direction B: Remote participant (English) -> User A hears Vietnamese
- Required visibility: Electron transcript panel shows both source and translated text

## 2. Pre-demo Setup

1. Start required WarpTalk services for meeting + transcript pipeline.
2. Open Electron app and configure device mapping:
- Real microphone: physical mic used by User A
- Virtual speaker/cable input: meeting output capture device
- Virtual microphone output: translated voice fed into Zoom
- Real speaker/headphones output: translated remote voice for User A
3. In Zoom audio settings:
- Microphone: select virtual microphone device
- Speaker: select virtual speaker/cable output device
4. Confirm OS permissions for microphone/system audio capture.

## 3. Sanity Checks Before Live Script

1. Speak a short Vietnamese line locally and verify waveform/activity in Electron input capture.
2. Play a short English test clip from remote side and verify remote capture channel is active.
3. Confirm transcript panel receives partial/final updates.

## 4. Demo Script

### Script A - Vietnamese -> English

- Speaker: User A
- Vietnamese line: "Xin chao moi nguoi, hom nay chung ta demo WarpTalk cho cuoc hop ben thu ba."
- Expected result:
- Remote side hears English TTS equivalent through Zoom virtual microphone path.
- Transcript panel shows Vietnamese source segment and English translated segment.

### Script B - English -> Vietnamese

- Speaker: Remote participant
- English line: "Thanks, I can hear you clearly. Let's continue with the translation demo."
- Expected result:
- User A hears Vietnamese TTS through real speaker/headphones path.
- Transcript panel shows English source segment and Vietnamese translated segment.

## 5. Known Limitations

- Device naming collisions can cause wrong selection after reconnect.
- Platform audio processing (noise suppression/echo cancellation) may alter captured quality.
- Latency spikes may occur under heavy network or model load.

## 6. Fallback Playbook

### Device mismatch

1. Re-open Electron device selector.
2. Rebind all four device roles explicitly.
3. Re-run sanity checks.

### Permission failure

1. Re-enable OS microphone/screen audio permission.
2. Restart Electron capture pipeline.
3. Re-test with one short utterance before resuming script.

### Pipeline latency high

1. Pause live conversation and run scripted short sentences.
2. Continue in transcript-first mode if TTS delay exceeds acceptable demo threshold.
3. Call out latency limitation and continue showing direction-aware transcript updates.
