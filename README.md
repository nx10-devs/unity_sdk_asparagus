# Asparagus Unity SDK (`com.nx10.asparagus`)

High-frequency native motion, event-stream touch, game event, span, and state telemetry SDK for the Asparagus data stack (`asparagus-contract-v5`, Schema v3).

- **Zero external dependencies:** Pure C# (`netstandard2.1`) + built-in Unity modules + self-compiling Android (`.java`) and iOS (`.mm`) native ring-buffer sensor plugins.
- **Zero scene or prefab setup:** Automatically bootstraps its hidden `DontDestroyOnLoad` host (`[AsparagusSDK]`) on first session start.
- **Input plug-and-play:** Supports both Unity Input System (`com.unity.inputsystem`, linked optionally by GUID) and Legacy Input Manager (`ENABLE_LEGACY_INPUT_MANAGER`).
- **Total fault containment & zero hot-path GC:** Non-throwing public/lifecycle boundaries, off-main-thread Protobuf + GZip + persistent disk queue (`AsparagusWorker`), quantized delta + `Hold` folding, and strict W8-26 bounds splitting.

## Installation (Unity Package Manager)

### Option 1: Unity Package Manager UI
1. Open **Window -> Package Manager**.
2. Click **`+` -> Add package from git URL...**
3. Enter:
   ```text
   https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.0.0
   ```

### Option 2: `Packages/manifest.json`
```json
{
  "dependencies": {
    "com.nx10.asparagus": "https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.0.0"
  }
}
```

## Quickstart

```csharp
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;

// Start a session (zero scene/prefab wiring required; sensors stay off until 200 + batch_endpoint)
AsparagusSDK.StartSession("https://control-plane.example.com", yourApiKey);

// Send strongly-typed core events & spans
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

## Stage Testing & Optional Sample Harness

Before the Control Plane session-start route is live on stage, bootstrap directly with a minted stage token and batch endpoint (never hardcode tokens in source control):

```csharp
using Asparagus.Sdk;
using Asparagus.Sdk.Internal.Network;

AsparagusSDK.StartSessionWithStageOverride(new StageBootstrapConfig
{
    BatchEndpoint = stageBatchEndpoint,
    Token = stageBearerToken,
    IdOrg = orgId,
    IdDeployment = deploymentId,
    IdSource = sourceId,
    IdSession = sessionId
});
```

To try an interactive runtime test panel (Stage Override / Control Plane toggles, core event buttons, 1s desktop motion+swipe simulator, live status dashboard, and `.binpb` / `.binpb.gz` offline export), open **Window -> Package Manager -> Asparagus SDK -> Samples -> Stage Test & Diagnostics Harness -> Import**, add `AsparagusStageTestPanel` to any GameObject, and press **Play**.