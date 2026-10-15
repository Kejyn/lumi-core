# Changelog

## [Unreleased] - Input presentation and repository maintenance

- Report meaningful keyboard/mouse and gamepad activity through instance-owned device-family state and notifications.
- Keep prompt text and presentation in the consuming game; release native input hooks during shutdown.
- Updated installation, testing and contribution guides and excluded local credentials and generated artifacts.

## [Unreleased] - Release Candidate Polish, 2026-10-14

- Added instance-owned Input rebinding sessions, completion/cancellation results and binding change notifications.
- Added bounded path-only override import/export and reset, validated on a clone before active mutation.
- Captures suspend configured maps and UI ownership, support composite parts and cancel/timeout, and release native hooks on completion, context change and shutdown.
- Kept UI, save storage, menu presentation and binding reservation policy in the consuming project; no additional module or dependency.

## [Unreleased] - Phase 5, 2026-10-14

- Added installation, seven-module composition, upgrade and candidate release documentation.
- Validated reuse through a blank Unity 6000.3.13f1 consumer using public APIs and its own scenes, input, UI, mixer and DTO.
- Preserved package version 0.1.0, dependencies and all seven runtime implementations; final release tags await explicit user validation.

## Phase 4 - 2026-10-13

- Added independent Persistence assembly with instance-owned ILumiService lifecycle and explicit game/model identities.
- Added diagnostic Save/Load/Delete/Exists/List APIs, UTC metadata, separate file/data versions and game-owned validators.
- Added strict native JsonUtility DTO contracts and envelope SHA256 integrity checking, without gameplay models or external dependencies.
- Added bounded UTF-8 disk storage, same-directory staging, Flush(true), native replacement with previous-file backup and exclusive game-directory ownership.
- Added backup recovery, preservation of corrupt/future data, serialized cancellable operations and shutdown draining committed writes.
- Added isolated native disk and service regression tests, including actual Windows replacement failures.
- Preserved all six preceding runtime modules; Settings continues to use PlayerPrefs independently.

## Phase 3 - 2026-10-13

### Added

- Independent Settings assembly: configurable defaults, validated snapshots, notifications, explicit graphics application and namespaced versioned PlayerPrefs storage.
- Graphics fallback for unavailable resolutions, display modes and quality profiles, with quality persisted by name and no fixed resolution or render pipeline.
- Independent Audio assembly: native mixer groups/parameters, exact mute for owned sources, one music source with unscaled latest-request fades and a bounded reusable 2D/3D SFX pool.
- Lifecycle cleanup, shared audio startup, cancellation and partial-startup rollback; sound assets and Settings-to-Audio composition remain in the consuming project.
- Settings and native Audio regression tests, including real PlayerPrefs service restart and rejection of unloaded clips.

### Corrected during validation

- Detect missing/null root settings values explicitly: Unity JsonUtility can instantiate that field even when the stored JSON omits it.
- Validate known JSON field types and reject duplicate known keys before native deserialization can silently coerce malformed numeric preferences to zero. Future integer schemas remain preserved without applying V1 field rules.
- Stop obsolete notifications after reentrant changes or shutdown; return the snapshot committed by the calling Update.
- Canonicalize the configured default language so invalid values use the same supported spelling without spurious change notifications.
- Reject unloaded/background-loading clips before playback so pending native playback cannot be mistaken for a finished voice.

Bootstrap, Scene Flow, Input and UI runtime implementations are unchanged.
Persistence, Localization, Dialogue, AI and asset-tool modules remain outside scope.

## Phase 2 - 2026-10-13

### Added

- Independent Input assembly: owned InputActionAsset clone, configurable Gameplay/UI/Pause maps, suspension leases and disposable callbacks.
- uGUI assembly: screen registration, history, modal stack, EventSystem selection and configurable Input System bridge.
- Pause service with opt-in time scaling, exact context/time restoration and reentrancy protection.
- Required Unity package declarations: Input System 1.19.0 and uGUI 2.0.0, already installed in the reference project.
- Input/UI unit coverage and Starter interaction/repeated Play-session integration coverage.

### Corrected during validation

- Input shutdown notifies the UI bridge before destroying its cloned asset.
- The bridge acquires ownership of its six UI actions to release Input System 1.19's static action references on unbind.
- Reentrant pause/resume cannot leave time frozen after resuming.
- A newly bound UI module receives a stationary pointer's initial position; the runtime clone's binding cache is rebuilt without changing the source.

Bootstrap and Scene Flow implementations are unchanged. Settings, Audio, Persistence and Localization are not implemented.

## [0.1.0] - 2026-10-13

### Added

- Local UPM package `com.lumivale.lumi-core` targeting Unity 6000.3.13f1.
- Independent Bootstrap and Scene Flow runtime assemblies.
- Deterministic dependency-driven service startup, rollback and reverse shutdown.
- Persistent bootstrap host with duplicate initialization protection.
- Asynchronous scene loading with exclusive access, configurable transitions and progress/completion/error events.
- Reference-project demonstration and EditMode/PlayMode validation suites.
- Reserved module boundaries for Input, Settings, Audio, UI and Persistence.

### Audited

- Moved the 35 Core unit tests into Tests/Editor with their original .meta GUIDs and a package-owned test assembly.
- Kept project/scene integration tests in Lumi-start and added repeated Play-session coverage without Domain Reload.
- Documented event subscription ownership, transition reentrancy limits and custom loader requirements.
- Added Git exclusions for UPM development caches and generated IDE files. Runtime API and implementation are unchanged by the audit.
