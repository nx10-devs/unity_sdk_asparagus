# Changelog

All notable changes to the Asparagus Unity SDK (`com.nx10.asparagus`) are documented in this file.

## [1.1.0] - 2026-11-27

### Added
- `asparagus-contract-v6` & `asparagus-contract-v7` compliance:
  - 10th core span `CoreSpans.Banner = "banner"` in `CoreSpans.All` (preserving `CoreSpans.AdForType("banner") == "ad_banner"`).
  - `CollectionHistory` (`SessionStartRequest.History`, `session.proto` field 10) serialized in Protobuf JSON with `"collected_seconds_before"` as a quoted JSON string (and omitted when all fields are zero), persisted atomically off the main thread in `asparagus_history.dat` via `CollectionHistoryStore` + `AsparagusWorker`.
  - `SessionStartResponse.SettingsLabel` (`settings_label`, `session.proto` field 13) parsed cleanly and exposed alongside `CollectionHistory` counters in `AsparagusStatusSnapshot`.
- Hardcoded Control Plane base URL (`https://control-plane.affectstack-stage.com` -> `/routes/asparagus/sessions/start`) and 1-argument `AsparagusSDK.StartSession(apiKey)` developer entrypoint.

### Changed
- Removed obsolete `defaults.json` contract copies per Decision 159 (SDK uses built-in `SessionSettings` safety-net defaults) and updated `Contracts~/` to `asparagus-contract-v7`.
- Updated `Samples~/StageTestHarness/AsparagusStageTestPanel.cs` to configure `_apiKey`, `_autoStartSessionOnStart`, `_demoMode`, and optional app/build overrides via Unity Inspector `[SerializeField]` fields (removing the legacy Connection Mode UI).
- Marked `AsparagusSDK.StartSessionWithStageOverride` and `AsparagusSDK.UpdateStageToken` as `[Obsolete]`.

## [1.0.0] - 2026-11-27

### Added
- Full `asparagus-contract-v5` (Schema v3) Protobuf and Session JSON wire codecs with zero external dependencies (`netstandard2.1`, C# 9.0, zero `System.Linq`).
- Monotonic `SessionClock` (server-anchored, whole-second windowing, pause/resume `CLOCK_STEPPED` & `new_session_after_s` expiry), `StateTracker` (`nx10_*` reserved key protection, SDK brightness/lock updates), `EventAndSpanTracker` (all 18 span/event rules), and strongly-typed `CoreEventSender` + `CoreSpans`.
- Zero-GC quantized delta + `Hold`/`TouchHold` stream encoders with W0-10 magnetometer exclusion and Unity Input System / Legacy Input Manager capture sources.
- Native Android (`AsparagusSensorPlugin.java` + `proguard-user.txt`) and iOS (`AsparagusSensorPlugin.mm`) off-thread ring-buffer sensor plugins with silent per-sensor fallback and explicit UPM `PluginImporter` `.meta` platform configuration.
- Proactive W8-26 bounds splitting (`BatchWindowCoordinator`), `AutoResetEvent` off-main-thread `AsparagusWorker`, atomic write-before-upload `DiskBatchQueue` with cross-session recovery and atomic dropped-batch rollback, and full HTTP status matrix transport (`AsparagusTransportCoordinator` + `UnityWebRequestHttpTransport`).
- Auto-bootstrapped fault-contained `AsparagusSDK` facade (`[AsparagusSDK]` `DontDestroyOnLoad` host), IL2CPP `link.xml`, 52 EditMode NUnit tests, and optional **Stage Test & Diagnostics Harness** UPM sample.
