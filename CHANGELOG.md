# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **`LiveTalkAPI.RenderExpressionAsync(character, expression, seconds, …)`**
  — the frames of one of an avatar's expressions, in playback order, for
  a given duration, cycling if the duration outlasts the expression.
  A negative duration means the expression's own length: exactly one
  cycle of its driving clip, which is the duration to ask for when the
  result has to loop, since the clips close on themselves and any other
  length cuts mid-motion. Normally nothing is inferred — an avatar
  renders every expression when it is built, so this resolves to those
  stored PNGs (the same files `CharacterPlayer` plays as its idle loop
  and a performance serves for stored ticks) and returns their paths.
  Absent frames are re-rendered from the expression's recorded poses
  (`motion.bin`, avatars at `Avatar.Version` ≥ 2) into the cache.
  Hosts wanting an idle clip no longer have to reach for
  `Character.IdleFramesFolder`, which stays internal.

## [2.3.0] - 2026-09-07

Scripted scenes are a first-class API: an expression track and a speech
track on one 25 fps clock, rendered once into the cache and streamed
from disk. Lip-sync frames now key on the wav content hash so a
re-rolled take misses automatically. Existing avatar folders rebuild
once (`Avatar.Version` 2). `CharacterPlayer` is unchanged.

### Changed
- **Chat and performance lip-sync frames key on the wav content hash**,
  not only voice + text + face (`frames_cache_v3`, `perf_mouth_v2`).
  A re-rolled take of the same line misses automatically; the host does
  not delete folders. Voice-only (`expressionIndex: -1`) still does not
  run MuseTalk. File length is not enough: two takes of the same
  duration were a false hit. v2 / `perf_mouth_v1` folders are simply
  never matched; clear the cache to reclaim the space.
- **Performance lip-sync is cached per utterance slice**, not per
  `perf_*` folder. Mouths key on voice + text + avatar + the hash of
  the base-face sequence under the line (stored expression/frame or
  pose-cache key, in order) and the wav content hash. A new fingerprint
  that only moves cues still on the same faces reuses those mouths;
  MuseTalk runs only for slices whose plan actually changed. Chat
  (`CharacterPlayer`) keys on expression index plus the same wav hash.
  Incomplete folders are not a hit.

### Added
- **`Character.SpeakAsync(..., useCache:)`** (default true) and
  **`CharacterPlayer.QueueSpeech(..., useCache:)`**. False skips the
  audio cache *read* for that call so the same voice + text synthesises
  a new take, then overwrites that wav. Frames for that take miss on
  the next animated speak because they hash the wav. `expressionIndex:
  -1` never runs MuseTalk. Independent of
  `LiveTalkAPI.SetCacheEnabled`, which is a global on/off.
- **`LiveTalkAPI.IsPerformanceRendered`** — whether a performance's
  cache folder is already complete, so a host can preview audio on the
  first bake and skip that pass on replay.
  `Performance` holds `ExpressionCue`s (an expression the face performs:
  play the clip through, or play to its peak and hold it with the idle
  clip's own micro-motion, eyes at 1.0 so blinks stay real) and
  `Utterance`s (a line by any character, lip-synced or audio-only),
  each placed by an `Anchor` — absolute seconds, or relative to another
  cue's start or end, so a reaction can be authored against a line whose
  length is only known once its audio exists, and lines can overlap
  (an interruption). `LiveTalkAPI.RenderPerformanceAsync` renders it
  once into the cache: audio per utterance (cached on voice + text), the
  track resolved to a pose per tick, then lip-sync per utterance over the
  base frames the track has at those ticks; `PerformancePlayer` streams
  the result — frames per animated character, wavs, captions — from disk
  a little ahead of the play head. `CharacterPlayer` is unchanged and
  remains the right tool for a chat where the next line is not known.
- **`LiveTalkAPI.RenderPosesAsync`** — full frames from final LivePortrait
  driving poses (63 floats), no motion extractor. The primitive behind
  blends and holds; also usable on its own.
- **Avatar v2 records the driving pose per frame** (`motion.bin` per
  expression; `Avatar.Version` 2). Existing avatar folders rebuild once.
  A blend is a `lerp` between two authored poses; a hold is the peak pose
  plus the idle clip's delta from its rest. No gain, no scale pin — this
  is not the 2.1 motion editing.
- **`Tools~/driving_clips/build.py`** — one command to generate
  `Resources/driving/*.mp4` from scratch. Look, character, framing and
  encode knobs live in the SETTINGS block at the top of that file;
  `python3 build.py` installs MB-Lab, dresses the character, renders and
  encodes. See `Tools~/driving_clips/README.md`.

## [2.2.0] - 2026-09-05

LivePortrait crops are upright (they were rotated ~40°), the bundled driving clips are new authored 25 fps rest-to-rest footage, idle loops forward, and speech continues from the idle frame. Existing avatar folders rebuild once.

### Added
- **Authored driving clips** (`Resources/driving/*.mp4`, pipeline in `Tools~/driving_clips/`). A rendered face, 512×512, 25 fps, rest-to-rest: a 27 s idle and six expression takes (approve, disapprove, smile, sad, surprised, confused).
- **`Avatar.Version` in the avatar id.** Bump it when the driving recipe or clips change so folders rebuild.
- **`Character.SpeakAsync(..., startFrameIndex:)`** — the avatar frame the first lip-sync frame is rendered onto. `CharacterPlayer.SpeechContinuity` (on by default) starts speech from the idle frame that will be showing when generation finishes, and resumes idle from where speech ended.

### Fixed
- **Face crops were rotated ~40°.** `FaceAnalysis.ParsePt2FromPtX` used 106-point eye/lip indices on the 203-point `LandmarkRunner` output. The crop went in tilted and ~14 % small: weak expressions, blinks that overshot, features drifting sideways, extractor `scale` swinging with the jaw. Dispatch on landmark count (`ParsePt2FromPt203`). Frame-0 roll −40.6° → −0.23° (Python reference −0.18°).
- Motion extractor pitch was read from the yaw output; nods were lost and turns tilted.
- `CharacterPlayer` reported the previous frame's index from `OnFrameUpdate`.
- Dropped `Resources/00000000.png`, an unreferenced 853 KB sample that shipped in every consumer build.

### Changed
- **Idle is a forward loop** at the clip's 25 fps. Speech does not restart the clip.

## [2.0.0] - 2026-09-04

A breaking release. The TTS backend moved from Spark-TTS to Qwen3-TTS, which changes the dependency and invalidates 1.x voice folders; the character folder format changed from copies to references; and the speech/frames cache keys changed. The `IEnumerator` + `onComplete` / `onError` API style is unchanged and is the primary style going forward. See **Migration** at the end of this section.

### Added
- **Experimental streaming lip-sync, off by default** (`LiveTalkAPI.StreamLipSync`, `StreamLipSyncContextSeconds`, `CharacterPlayer.PrerollSeconds`). With it on, each TTS chunk is fed to an incremental Whisper feature extractor (`StreamingAudioFeatures`) that captures the log-mel `ref=max` from the first 0.5 s and holds it, emits only frames whose 200 ms feature window (plus the extra context) is fully inside the audio received so far, and runs the UNet on those frames while later chunks are still being synthesised; `CharacterPlayer` plays a `SpeechStream` gaplessly from a ring buffer, timing frames off the audio position and holding the last frame if generation falls behind. `Model` now clears its per-run inputs after each run (they accumulated for the life of a session; every UNet call held every earlier frame's tensors) — this fix applies to the batch path too and is bit-exact.

  Measured on two utterances (4.16 s / 104 frames and 5.2 s / 130 frames), mouth-band mean absolute difference against the batch path in /255: all-at-once through the refactored path **0.0000** (bit-exact); frozen reference alone 0.076 mean; streamed in 0.5 s chunks with 0.5 s extra context **0.505 / 1.03** mean (max 1.69 / 4.77), with 1.0 s extra context 0.297 / 0.87. The residual is the Whisper encoder's global self-attention seeing a prefix rather than the whole clip; it is not a margin, reference or alignment error and does not close by holding back more context. Time to first mouth movement on a 4 s line, warm models: batch **29.2 s**, streamed **2.4 s** — but on this hardware frame generation runs at ~100–200 ms/frame against 40 ms of audio, so playback holds the last frame while it catches up (60–75 frame deficit observed). Stop-mid-stream is implemented but was not exercised. Off (the default) is the batch path, unchanged and bit-identical to before; cache hits are unaffected either way. Knobs and behaviour may change without a major bump.
- **`Avatar`, `Voice` and `Character` are three entities with independent lifetimes.** A character used to be one folder whose identity hashed the portrait and the voice knobs together, so a new voice on the same portrait re-ran the three-minute avatar pass, and nothing could swap a character's voice. Now:
  - `Avatar` (`LiveTalkAPI.CreateAvatarAsync(image, mode, onComplete, onError)`, `LoadAvatarAsync(id, …)`) is the portrait plus its driving frames, latents and face crops. Its id is a content hash of the image bytes and the expression set (`HashUtils.GenerateAvatarId`), stored at `<saveLocation>/avatars/<id>/` (`image.png`, `avatar.json`, `drivingFrames/expression-N/…`). `CreateAvatarAsync` is get-or-create: an existing complete folder loads in seconds; otherwise the LivePortrait/MuseTalk pipeline runs and writes it. The avatar folder *is* the cache — characters reference it, nothing copies it.
  - `Voice` (`DesignVoiceAsync(gender, pitch, speed, instruct, sampleText, …)`, `CloneVoiceAsync(reference, transcript, …)`, `LoadVoiceAsync(id, …)`) is a saved speaker with `Kind` (`Designed` / `Cloned`), `Sample` and `SampleText`. A designed voice gets a new GUID per call — VoiceDesign samples a new speaker every time, so two rolls are two voices — and renders `sampleText` as its `Sample`, which is exactly what a host later locks. A cloned voice's id is a content hash of the reference PCM and transcript (`HashUtils.GenerateClonedVoiceId`), so cloning the same take twice loads the existing folder. Stored at `<saveLocation>/voices/<id>/` in the TTS engine's layout plus `voice.meta.json`.
  - `Character` (`LiveTalkAPI.CreateCharacter(name, avatar, voice)`) is a synchronous composition of the two: a GUID id, a name, and `character.json` = `{ id, name, avatarId, voiceId, speechSampleRate, createdUtc }` under `<saveLocation>/characters/<id>/`. Instant to create; `Character.Avatar` / `Character.Voice` expose the halves.
- `Character.ReplaceVoice(voice)` points a character at a different voice: rewrites `character.json` and drops any speech still queued or in flight in the old voice, so the next `QueueSpeech` speaks in the new one. No reprocessing.
- `Character.DestroyPlayer()` stops and destroys the `CharacterPlayer` GameObject; the next `CharacterPlayer` access creates a fresh one. Hosts that rebuild characters no longer leak idle players and AudioSources.
- `LiveTalkAPI.DeleteCharacter(id)`, `DeleteVoice(id)`, `DeleteAvatar(id)`. The latter two refuse (throwing, naming the characters) while any `characters/*/character.json` still references the id.
- `LiveTalkAPI.GetAvailableAvatarIds()`, `GetAvailableVoiceIds()`.
- `LiveTalkAPI.GetCacheSizeBytes(cacheLocation = null)`, and `ClearCache` takes an optional explicit `cacheLocation`. With an explicit location both work *before* `Initialize`, so a host can show and clear the cache without first paying for model setup.
- `Avatar.CanAnimate`, `Avatar.ExpressionIndices`, `Avatar.Mode`; `Voice.Gender` / `Pitch` / `Speed` / `Instruct` (design parameters); `Character.IsLegacy`, `Character.CreatedUtc`.
- `EnsureRuntimeHost()` recreates the coroutine GameObject after Play teardown while the API singleton is still initialized.
- `UnloadTts()` drops the TTS engine's ONNX sessions without disposing LivePortrait / MuseTalk.
- `VoiceModelsLoaded` reports whether the TTS engine currently holds models.
- `voiceInstruct` on character creation, passed through to voice design. `Gender`/`Pitch`/`Speed` are composed into a natural-language description in `Utils/VoiceInstruct`, which is host policy rather than engine behaviour.
- `CharacterPlayer.OnReady` fires once character data *and* idle frames are loaded — the moment the player enters the new `PlaybackState.Ready`. `OnCharacterLoaded` still fires right after it. `CharacterPlayer.IsReady` reports that state.
- `PlaybackState.Ready` replaces `Idle`, which is kept as an `[Obsolete]` alias with the same value. The state machine is `Uninitialized → Loading → Ready ⇄ Speaking`, plus `Paused`.
- `LiveTalkAPI.WarmUpVoiceAsync(QwenCheckpoint)` loads a TTS checkpoint off the main thread (the talker graphs take ~10 s to open cold; without it the first line pays all of it), and `LiveTalkAPI.EvictVoice(QwenCheckpoint)` releases one checkpoint while keeping the other. The two checkpoints are wanted in different phases — VoiceDesign while choosing a voice, Base for cloning and speaking — and are ~7 GB resident each, so hosts should drop VoiceDesign once a take is locked. `WaitForAllModelsAsync` deliberately no longer warms the TTS.
- `Character.SpeakAsync` takes an optional `onSpeechChunk` callback and streams speech as it is generated (first chunk in about a second, main thread, only new samples per call). Ignored on a cache hit.
- `Character.SpeechSampleRate`. 16 kHz for a character with an animatable avatar (what the lip-sync stack consumes); the TTS model's native rate for a voice-only character, where a 16 kHz round trip would throw away the top of the band a clone reference is identified by. Persisted in `character.json`.
- `voiceCloneRefText` on `CreateCharacterAsync`: the transcript of the clone reference, which is what makes the clone in-context (reproducing the speaker) rather than a speaker-embedding match.
- `Initialize(ttsModelRoot:)` points the TTS engine at the folder holding its checkpoints instead of assuming `StreamingAssets/QwenTTS`.
- `package.json` declares `repository`, `documentationUrl`, `changelogUrl`, `licensesUrl`, `license` (MIT) and a `samples` entry; the demo moved to `Samples~/LiveTalkDemo/` (UPM sample convention, imported through the Package Manager) with a README.

### Changed
- **TTS backend is Qwen3-TTS** (`com.genesis.qwentts.unity`, via [Qwen3-TTS-Unity](https://github.com/arghyasur1991/Qwen3-TTS-Unity)) instead of Spark-TTS. Voice design takes a natural-language description (composed from `Gender` / `Pitch` / `Speed` plus optional free text); cloning takes a reference recording plus its transcript. 1.x voice folders were Spark-TTS artefacts and cannot be reused. `package.json` also declares `com.github.asus4.onnxruntime`, `com.unity.nuget.newtonsoft-json` and `com.unity.ugui`, which the runtime assembly always required; the Qwen package must still be added by git URL first (see README → Install).
- **Character folders hold references, not copies.** A 2.0 character is `characters/<id>/character.json` pointing at an avatar id and a voice id; it no longer contains `image.png`, `drivingFrames/` or `voice/`. Several characters share one avatar folder, and deleting a character leaves both halves for the others. Pre-2.0 inline folders (`<saveLocation>/<id>[.bundle]/` with everything beside `character.json`) still load in place through `LoadCharacterAsyncFromId` / `LoadCharacterMetadataAsync` / `GetAvailableCharacterIds`, logged once as legacy; they cannot have their voice replaced and their halves cannot be shared or deleted on their own. Recreate through the 2.0 API to migrate. The 2.0 layout does not write macOS `.bundle` folders; legacy bundles are still read.
- **Speech and frames cache keys are voice-based and include the expression.** Audio is cached on `hash(voiceId, text)` (`HashUtils.GenerateSpeechCacheKey(voiceId, text)` — the parameter order changed) and lip-sync frames on `hash(voiceId, text, avatarId, expressionIndex)` (`HashUtils.GenerateFramesCacheKey`). Previously both keyed on `text + characterId` and omitted the expression, so the same line at expression 0 and expression 3 replayed the same face, and a re-rolled voice under a stable character id served the old take. The salts are bumped (`voice_cache_v2`, `frames_cache_v2`), so every entry written by the old layout is simply never matched again; clear the cache to reclaim the space.
- `Character.CharacterId` is read-only (`Character.Id` is the same value). A character's id is a GUID assigned at creation and never changes.
- `Character.SpeechSampleRate` is persisted in `character.json`. 16 kHz for a character with an animatable avatar, native rate for voice-only; a legacy file without the field gets the same rule applied on load.
- `Character.Image` is the avatar's image once loaded (or just the image file after a metadata load). `Character.Gender` / `Pitch` / `Speed` / `Intro` / `VoiceInstruct` / `VoiceCloneRefText` / `VoicePromptClip` remain as `[Obsolete]` forwards to the corresponding `Voice` members.
- `Character.CharacterPlayer` is created lazily on first access only; a full load no longer creates one eagerly (which could leave a second, orphaned player when the host had made its own).
- `CharacterPlayer` reads idle frames from the character's avatar (expression 0) rather than from a `drivingFrames/` folder beside `character.json`.
- A failed `CreateAvatarAsync` / `DesignVoiceAsync` / `CloneVoiceAsync` removes its partial folder: each writes into a `<id>.partial-…` staging folder that no id lookup matches and moves it into place only when complete (`avatar.json` / `voice.meta.json` are written last). Staging folders left by a crash are swept on `Initialize`. An avatar folder found incomplete is rebuilt rather than reused; an expression folder without `latents.bin` / `faces.json` fails the load instead of loading empty.
- `CharacterPlayer.IsPlaying` is true only while `Speaking`. It used to include `Idle`, so it was true for the whole life of a loaded player and useless as a "finished" test. Hosts that want "nothing left to say" should test `!IsPlaying && QueuedSpeechCount == 0` or listen for `OnSpeechEnded`.
- `CharacterPlayer.QueueSpeech` may be called as soon as a character is assigned. While the player is `Loading` the request is only queued and drains on `OnReady`; previously it was dropped with a warning, forcing every host to poll the state before speaking.
- `CharacterPlayer.Pause()` also stops the idle animation and holds frame playback (previously frame playback exited and the next segment started over a paused AudioSource). `Resume()` returns to `Speaking` only if the player was speaking when paused; otherwise to `Ready` with idle. Speech queued while paused starts on `Resume()`.
- `CharacterPlayer.Stop()` no longer aborts a character load in progress — loading is not playback — and the player returns to `Ready` (with idle) once loaded. `AssignCharacter` still cancels the previous character's load.
- `CharacterPlayer.ParentTransform` caches the shared parent instead of running `GameObject.Find` on every access.
- `CharacterPlayer` and `DialogueOrchestrator` log through `Logger`, so they honour `LogLevel` like the rest of the runtime.
- `ModelUtils` uses the default `OrtEnv` and forwards log attribution to whichever library created the environment. ONNX Runtime allows one environment per process, so creating a second one with its own sink meant LiveTalk's model names never reached the sink that was actually installed. The TTS engine is initialized first so it owns the environment.
- `LogLevel.VERBOSE` opts into ONNX Runtime's own INFO and VERBOSE output; `INFO` maps ORT to WARNING, because ORT at INFO emits an arena line per allocation and buried LiveTalk's own lines. `ERROR` maps to ERROR.
- Audio helpers delegate to the TTS package's `QwenAudio` rather than carrying their own copies. `ConcatenateAudioClips` now honours its `silenceDuration` argument, which the previous delegating version ignored, and WAV load/save is local in `AudioFileIO`.
- `FrameStream` exposes `Error`, set when the producer that fills it failed; the stream is still marked finished so consumers drain and exit.
- `HashUtils`, `AudioUtils`, `TextUtils` and `StringUtils` are `internal`. They were never documented as API and nothing outside the runtime assembly used them.
- The voice-preview default sample text is a neutral sentence.
- The editor assembly no longer references the TTS package; it never used it.
- The demo sample uses `UnityEngine.UI.Text` instead of TextMeshPro, so it compiles with no packages beyond LiveTalk's own, and it creates characters through `CreateAvatarAsync` + `DesignVoiceAsync` + `CreateCharacter`.
- `.gitignore` is a package one (OS, IDE, Unity project folders, Python by-products, `*.onnx`) rather than the Unity project template.

### Deprecated
- Both `LiveTalkAPI.CreateCharacterAsync(name, gender, image, pitch, speed, intro, voicePromptPath, …)` overloads. They now forward to `CreateAvatarAsync` (when `image` is non-null) + `CloneVoiceAsync` (when `voicePromptPath` is given) or `DesignVoiceAsync` (with `intro` as the sample text) + `CreateCharacter`, and produce a 2.0 reference character with a GUID id; `useBundle` is ignored. Use the three calls directly.
- `VoicePreviewResult` and `GenerateVoicePreviewAsync`. The preview is now a real saved `Voice` (returned in `VoicePreviewResult.Voice`; `VoiceFolderPath` is its `voices/<id>` folder), built on `DesignVoiceAsync`, so a chosen preview goes straight into `CreateCharacter` and a rejected one is removed with `DeleteVoice`. `CleanupVoicePreviews` / `DeleteVoicePreview` likewise.
- `Character.Gender`, `Pitch`, `Speed`, `Intro`, `VoiceInstruct`, `VoiceCloneRefText`, `VoicePromptClip` — read them from `Character.Voice`.

### Removed
- The voice-style cache (`HashUtils.GenerateVoiceStyleCacheKey`, `vs_*` folders). It was keyed on the character id, so a "re-rolled" designed voice hit the cache and came back as the same speaker. A designed voice is an explicit, saved `Voice` now.
- The driving-frames cache (`HashUtils.GenerateDrivingFramesCacheKey`, `df_*` folders, `LiveTalkCache.CopyFolder` / `CheckFolderExists` / `CheckFolderTreeExists` / `GetFolderPath`). It only mitigated the rebuild by copying hundreds of MB into each new character; the avatar folder replaces it outright. (`CheckFolderTreeExists` was added earlier on this same branch to make nested cache folders match, and is removed again here.) Existing `df_*` / `vs_*` entries in a cache folder are dead weight — `ClearCache` removes them.
- `Character.CreateAvatarAsync(voicePromptPath, useBundle, creationMode, onError)` — the avatar pipeline lives in `Avatar`, reached through `LiveTalkAPI.CreateAvatarAsync`.
- The public setter on `Character.CharacterId`.
- The `LiveTalkAPI` finalizer. `Dispose(false)` released nothing — every resource is managed and only freed on the disposing path — and a finalizer runs on the GC thread, where nothing this class owns (ONNX sessions, the coroutine host GameObject) may be touched. `Dispose()` is unchanged.
- The Spark-TTS dependency (`com.genesis.sparktts.unity`), and every reference to it in the README.

### Fixed
- A model that fails to load is reported as that failure. The coroutines that awaited `StartSession` / `StartGeneratorSession` waited on `task.IsCompleted`, which a faulted task also satisfies, so a missing weights file was never observed and the inference loop walked on until the first model it touched said "Model is not initialized" — several layers from the cause. The load fault is now observed and reported with its original exception (superseded, on this same branch, by the general `TaskYield` bridge below).
- Faulted tasks inside coroutines are no longer skipped. The pipeline waited on `task.IsCompleted`, which is also true for a faulted task, so a failed model load, synthesis or file read was silently passed over and surfaced later as a misleading error ("Model is not initialized", "Character voice not loaded", "Generated audio clip is null") — or not at all. Every such wait now goes through an internal bridge (`TaskYield.Wait`) that logs the original exception with its stack and rethrows it inside the coroutine.
- Frame producers (`LivePortraitInference`, `MuseTalkInference`, `LiveTalkController`) mark their `FrameStream` finished in `finally`, on every exit path. Previously a fault mid-generation left the stream open, the consumer waiting forever, and — for lip-sync — the MuseTalk lease never released, so the next animated line blocked on acquire with no error anywhere. A driving-frame or lip-sync producer that fails is now reported as a failure by its consumer rather than as a shorter clip, and a partially written frames-cache entry is deleted instead of being taken as a hit next time.
- `VoiceQueue` / `MuseTalkQueue` leases are acquired through the bridge and released in `finally`, so a fault or a stopped coroutine cannot leak the lock.
- `CreateCharacterAsync`, `CreateAvatarAsync`, `LoadCharacterAsync*` and `SpeakAsync` honour their `onError` contract: exactly one of `onComplete` / `onError` fires, and `onComplete` is never called with a half-built character. Voice design, voice clone and voice load throw with the offending path instead of logging and returning, and a character is not marked loaded when its voice is missing or failed to load. A failed animation still fires `SpeakAsync`'s `onError` after `onAudioReady` and never fires `onAnimationComplete`.
- Error messages no longer refer to a `CharacterFactory` that does not exist; the downstream messages that remain carry a hint to look for the earlier, original error.
- `CharacterPlayer` no longer resets its state to Idle when idle frames finish loading while a speech is in flight. `PlayFramesSynchronized` exits when the state is not `Speaking`, so that reset landed mid-line and produced audio with no lip-sync frames. Idle loading now leaves the state alone if anything is speaking, queued or running.
- Lines queued during the last segment of a speech are played. The processor loop exits when the queue empties, and a request that arrived after that sat in the queue forever once the player loop went idle. `QueueSpeech` now restarts the processor while `Speaking`, and the player loop re-checks the queue when it ends and continues (still `Speaking`, no intermediate `OnSpeechEnded`) instead of going idle first.
- `CharacterPlayer.Stop()` is complete: it stops the frame collectors (`CollectAnimationFrames` was started without a handle and survived), resets the pause bookkeeping, stops a paused AudioSource (which reports `isPlaying == false` and previously kept its clip), and retires the speech processor. The processor is retired rather than killed: while it is suspended inside `Character.SpeakAsync` the voice lease is held by that nested coroutine, and running a stopped iterator's `finally` blocks is not something Unity documents; the loop lets the in-flight synthesis finish on its normal path (where the lease is released), then sees the stale epoch and exits without touching state.
- The pipeline's running flags are set before `StartCoroutine`, not inside the coroutines, so two `QueueSpeech` calls in one frame cannot start a second processor or player loop.
- `DialogueOrchestrator` advances past the first line. It waited on `player.IsPlaying`, which was true while idle, so any multi-turn dialogue hung after the first segment. It now waits on `IsPlaying || Paused || QueuedSpeechCount > 0`.

### Migration
- **Install.** Add the OpenUPM scoped registry for `com.github.asus4`, then `com.genesis.qwentts.unity` from `https://github.com/arghyasur1991/Qwen3-TTS-Unity.git`, then this package. Remove `com.genesis.sparktts.unity`. Export the two Qwen3-TTS checkpoints (~8 GB each) and pass their folder as `Initialize(ttsModelRoot:)`.
- **Voices.** 1.x voice folders (Spark-TTS) do not load. Design or clone new voices with `DesignVoiceAsync` / `CloneVoiceAsync`.
- **Characters.** Existing inline folders (`<saveLocation>/<id>[.bundle]/`) still load through `LoadCharacterAsyncFromId` / `LoadCharacterMetadataAsync` / `GetAvailableCharacterIds` and speak, but are read-only (`Character.IsLegacy`): no `ReplaceVoice`, no sharing or deleting of their halves — and their inline voice is a Spark-TTS folder, so in practice they need recreating. To migrate, `CreateAvatarAsync` on the same portrait, make a voice, `CreateCharacter`, then `DeleteCharacter` the old id. The 2.0 layout never writes `.bundle` folders.
- **Creation code.** Replace `CreateCharacterAsync(name, gender, image, pitch, speed, intro, voicePromptPath, …)` with `CreateAvatarAsync` + (`CloneVoiceAsync` or `DesignVoiceAsync`) + `CreateCharacter`. The old overloads still compile and work (`[Obsolete]`), forward to those three, and produce a 2.0 character with a GUID id rather than a hash of the parameters.
- **Previews.** Replace `GenerateVoicePreviewAsync` / `VoicePreviewResult` with `DesignVoiceAsync`; the result is a saved `Voice` whose `Sample` / `SampleText` are the take. Discard rejected rolls with `DeleteVoice`.
- **Character properties.** `Character.Gender` / `Pitch` / `Speed` / `Intro` / `VoiceInstruct` / `VoiceCloneRefText` / `VoicePromptClip` → `Character.Voice.Gender` / `Pitch` / `Speed` / `SampleText` / `Instruct` / `SampleText` / `Sample`. `CharacterId` is read-only (`Id` is the same value).
- **Player.** `PlaybackState.Idle` → `PlaybackState.Ready` (same value). `IsPlaying` is true only while `Speaking`; code that used it as "loaded" should use `IsReady` or `OnReady`; code that waited for it to go false to mean "finished" now gets what it expected. `QueueSpeech` may be called before `Ready`. `Character.CharacterPlayer` is created on first access, not on load; call `Character.DestroyPlayer()` when rebuilding.
- **Cache.** Old entries are ignored (new key salts). Call `LiveTalkAPI.ClearCache()` — or `ClearCache(path)` before `Initialize` — to reclaim the space.
- **Memory.** `WaitForAllModelsAsync` no longer warms the TTS; call `WarmUpVoiceAsync(QwenCheckpoint.VoiceDesign)` / `(QwenCheckpoint.Base)` yourself and `EvictVoice` the one you are done with.

## [1.0.0] - 2025-07-04

Voice backend for this release: Spark-TTS (`com.genesis.sparktts.unity`). Replaced by Qwen3-TTS in 2.0.0.

### Added
- **LiveTalk-Unity Package**: Complete Unity package for real-time talking head generation
- **Dual-Pipeline System**: LivePortrait for facial animation + MuseTalk for lip synchronization
- **Character Creation System**: Create, save, and load characters with multiple expressions and voices
- **Advanced Character Management**: Support for both folder and macOS bundle formats
- **Complete API**: LiveTalkAPI singleton and Character class with full documentation
- **Expression Support**: 7 built-in expressions (talk-neutral, approve, disapprove, smile, sad, surprised, confused)
- **Integrated TTS**: Built-in SparkTTS integration for voice generation
- **Model Download Links**: Pre-exported ONNX models for both LiveTalk and SparkTTS
- **Cross-Platform Support**: macOS (CPU/CoreML), Windows (Not tested)
- **Performance Optimizations**: CoreML optimization for efficient on-device inference

### Features
- **Real-time Animation**: Generate talking head videos from avatar images and audio
- **Character Persistence**: Save and load character data with precomputed expressions
- **Voice Synthesis**: Create character voices with configurable pitch, speed, and gender
- **Frame Streaming**: Efficient frame-by-frame processing with coroutine support
- **Multiple Input Formats**: Support for images, videos, and directory-based driving frames
- **Bundle Support**: macOS package format for seamless character distribution
- **Memory Management**: Optimized memory usage with unsafe code and parallel processing

### Performance
- **Overall Performance**: 10-11 FPS for speech with lip sync on Mac M4 Max
- **Character Creation**: 10 minutes per character on Mac M4 Max
- **LivePortrait Pipeline**: 4 FPS
  - motion_extractor (FP32): 30-60ms
  - warping_spade (FP16): 180-250ms
  - landmark_runner (FP32): 2-3ms
- **MuseTalk Pipeline**: 11 FPS
  - vae_encoder (FP16): 20-30ms
  - unet (FP16): 30-40ms
  - vae_decoder (FP16): 40-50ms

### Requirements
- Unity 6000.0 or later
- Minimum 32GB RAM for character creation
- Storage space: ~10GB total (~7GB LiveTalk + ~3GB SparkTTS)
- macOS (CPU/CoreML) or Windows (Not tested)

### Dependencies
- com.github.asus4.onnxruntime (0.4.0)
- com.github.asus4.onnxruntime-extensions (0.4.0)
- com.unity.nuget.newtonsoft-json (3.2.1)

### License
- MIT License (following LivePortrait and MuseTalk licensing)
- Apache License 2.0 for SparkTTS components
