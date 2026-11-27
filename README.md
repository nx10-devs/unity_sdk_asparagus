# Asparagus Unity SDK (`com.nx10.asparagus`)

High-frequency native motion, event-stream touch, game event, span, and state telemetry SDK for the Asparagus data stack (`asparagus-contract-v7`, Schema v3).

- **Zero external dependencies:** Pure C# (`netstandard2.1`) + built-in Unity modules + self-compiling Android (`.java`) and iOS (`.mm`) native ring-buffer sensor plugins.
- **Zero-argument session start & central config:** Configure both Staging and Production API keys once in `Assets/Resources/AsparagusConfig.asset` via **Tools -> Asparagus SDK -> Edit Configuration** and call `AsparagusSDK.StartSession()` — lazily bootstraps its hidden `DontDestroyOnLoad` host (`[AsparagusSDK]`) with zero scene/prefab wiring.
- **Automatic Staging vs Production routing:** Switches cleanly between Staging (`https://control-plane.affectstack-stage.com`) and Production (`https://control-plane.affectstack.com`) and isolates `CollectionHistory` per environment (`asparagus_history_stage.dat` vs `asparagus_history.dat`).
- **Input plug-and-play:** Supports both Unity Input System (`com.unity.inputsystem`, linked optionally by GUID) and Legacy Input Manager (`ENABLE_LEGACY_INPUT_MANAGER`).
- **Total fault containment & zero hot-path GC:** Non-throwing public/lifecycle boundaries, off-main-thread Protobuf + GZip + persistent disk queue + environment-scoped `CollectionHistory` persistence (`AsparagusWorker`), quantized delta + `Hold` folding, and strict W8-26 bounds splitting.

## Installation (Unity Package Manager)

### Option 1: Unity Package Manager UI
1. Open **Window -> Package Manager**.
2. Click **`+` -> Add package from git URL...**
3. Enter:
   ```text
   https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.2.0
   ```

### Option 2: `Packages/manifest.json`
```json
{
  "dependencies": {
    "com.nx10.asparagus": "https://github.com/nx10-devs/unity_sdk_asparagus.git#v1.2.0"
  }
}
```

## Configuration (`Tools -> Asparagus SDK -> Edit Configuration`)

1. In Unity, click **Tools -> Asparagus SDK -> Edit Configuration**. This creates and selects `Assets/Resources/AsparagusConfig.asset`.
2. Enter your **Staging Api Key** and **Production Api Key** (or supply `ASPARAGUS_STAGING_API_KEY`, `ASPARAGUS_PRODUCTION_API_KEY`, or `ASPARAGUS_API_KEY` via environment variables). Never commit real secrets to public Git repositories.
3. Choose **Environment Mode**:

| Environment Mode | Unity Editor & Development Builds | Release Player Builds | Control Plane Base URL |
| --- | --- | --- | --- |
| `Automatic` (default) | Staging | Production | Switches automatically |
| `Staging` | Staging | Staging | `https://control-plane.affectstack-stage.com` |
| `Production` | Production | Production | `https://control-plane.affectstack.com` |

Session start route: `POST /routes/asparagus/sessions/start`.

## Quickstart

```csharp
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;

// Start a session using Assets/Resources/AsparagusConfig.asset (zero arguments!)
AsparagusSDK.StartSession();

// Optional: configure or override in code (e.g., from CI/CD or runtime config)
// AsparagusSDK.Configure(new AsparagusConfiguration
// {
//     EnvironmentMode = AsparagusEnvironmentMode.Automatic,
//     StagingApiKey = stagingKey,
//     ProductionApiKey = prodKey
// });

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

## Optional Sample Harness

To try an interactive runtime test panel (config-backed Staging/Production switching, core event & `CoreSpans.Banner` buttons, 1s desktop motion+swipe simulator, live status dashboard with `ActiveEnvironment`, `ControlPlaneBaseUrl`, `SettingsLabel`, and `CollectionHistory`, and `.binpb` / `.binpb.gz` offline export):

1. Open **Window -> Package Manager -> Asparagus SDK -> Samples -> Stage Test & Diagnostics Harness -> Import**.
2. Configure keys via **Tools -> Asparagus SDK -> Edit Configuration**.
3. Add `AsparagusStageTestPanel` to any GameObject in your scene and press **Play**.
