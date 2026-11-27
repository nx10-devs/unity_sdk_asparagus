# Asparagus Unity SDK (`com.nx10.asparagus`)

High-frequency native motion, event-stream touch, game event, span, and state telemetry SDK for the Asparagus data stack (`asparagus-contract-v7`, Schema v3).

- **Zero external dependencies:** Pure C# (`netstandard2.1`) + built-in Unity modules + self-compiling Android (`.java`) and iOS (`.mm`) native ring-buffer sensor plugins.
- **One-argument session start & zero scene wiring:** Call `AsparagusSDK.StartSession(yourApiKey)` — targets `https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start` automatically and lazily bootstraps its hidden `DontDestroyOnLoad` host (`[AsparagusSDK]`).
- **Input plug-and-play:** Supports both Unity Input System (`com.unity.inputsystem`, linked optionally by GUID) and Legacy Input Manager (`ENABLE_LEGACY_INPUT_MANAGER`).
- **Total fault containment & zero hot-path GC:** Non-throwing public/lifecycle boundaries, off-main-thread Protobuf + GZip + persistent disk queue + `CollectionHistory` persistence (`AsparagusWorker`), quantized delta + `Hold` folding, and strict W8-26 bounds splitting.

## Installation (Unity Package Manager)

### Option 1: Unity Package Manager UI
1. Open **Window -> Package Manager**.
2. Click **`+` -> Add package from git URL...**
3. Enter:
   ```text
   https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.1.0
   ```

### Option 2: `Packages/manifest.json`
```json
{
  "dependencies": {
    "com.nx10.asparagus": "https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.1.0"
  }
}
```

## Quickstart

```csharp
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;

// Start a session (zero scene/prefab wiring required; sensors stay off until 200 + batch_endpoint)
AsparagusSDK.StartSession(yourApiKey);

// Send strongly-typed core events & spans (including CoreSpans.Banner)
AsparagusSDK.SendCoreEvent.LevelStarted("1", attemptNumber: 1, difficulty: 0.5);
AsparagusSDK.SetAttribute("game_level", "1");
AsparagusSDK.SendCoreEvent.LevelSucceeded("1", score: 1250, duration: 42.5);

// Send custom events or open/close standard spans
AsparagusSDK.SendEvent("popup_opened", null, CoreSpans.Popup);

// Pause or stop telemetry cleanly at any time
AsparagusSDK.SetTouchPaused(true);
AsparagusSDK.StopTelemetry();
AsparagusSDK.StartTelemetry();
AsparagusSDK.StopSession();
```

## Optional Sample Harness (Unity Inspector Setup)

To try an interactive runtime test panel (Control Plane session controls, core event buttons, 1s desktop motion+swipe simulator, live status dashboard with `SettingsLabel` and `CollectionHistory`, and `.binpb` / `.binpb.gz` offline export):

1. Open **Window -> Package Manager -> Asparagus SDK -> Samples -> Stage Test & Diagnostics Harness -> Import**.
2. Add `AsparagusStageTestPanel` to any GameObject in your scene, paste your deployment API key into the **Api Key** field in the Unity Inspector (or set the `ASPARAGUS_API_KEY` environment variable — never commit a real API key to Git), and press **Play**.
