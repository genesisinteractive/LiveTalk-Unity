# LiveTalk-Unity

On-device talking-head characters for Unity. Give it a portrait and a line of
text; get back speech and a lip-synced, expressive video stream, all generated
locally through ONNX Runtime.

LiveTalk combines three models behind one API:

| Piece | Does | Upstream |
|---|---|---|
| **LivePortrait** | Animates a still portrait with expressions and head motion | [KwaiVGI/LivePortrait](https://github.com/KwaiVGI/LivePortrait) |
| **MuseTalk** | Lip-syncs those frames to audio in real time | [TMElyralab/MuseTalk](https://github.com/TMElyralab/MuseTalk) |
| **Qwen3-TTS** | Designs a speaker from a description, or clones one from a recording, and speaks | [Qwen3-TTS-Unity](https://github.com/arghyasur1991/Qwen3-TTS-Unity) |

The PyTorch models of the first two were exported to ONNX and tuned for CoreML;
the third is a separate package this one depends on. 2.0 changed the voice
backend and the storage layout — see [Migrating from 1.x](#migrating-from-1x).

## Contents

- [The 2.0 model: Avatar, Voice, Character](#the-20-model-avatar-voice-character)
- [Install](#install)
- [Models](#models)
- [Initialize](#initialize)
- [Create an avatar](#create-an-avatar)
- [Design a voice (roll the dice)](#design-a-voice-roll-the-dice)
- [Clone a voice from a take](#clone-a-voice-from-a-take)
- [Create a character](#create-a-character)
- [Speak with CharacterPlayer](#speak-with-characterplayer)
- [Speak directly with SpeakAsync](#speak-directly-with-speakasync)
- [DialogueOrchestrator](#dialogueorchestrator)
- [Performances (scripted scenes)](#performances-scripted-scenes)
- [Raw animation without a character](#raw-animation-without-a-character)
- [Caching](#caching)
- [Error handling contract](#error-handling-contract)
- [Memory](#memory)
- [Migrating from 1.x](#migrating-from-1x)
- [Requirements and performance](#requirements-and-performance)
- [License](#license)

## The 2.0 model: Avatar, Voice, Character

Three entities with independent lifetimes, all under
`LiveTalkAPI.CharacterSaveLocation` (default
`Application.persistentDataPath/Characters`):

```
<saveLocation>/
  avatars/<avatarId>/         image.png, avatar.json, drivingFrames/expression-N/…
  voices/<voiceId>/           voice.json, clone_prompt.bin, reference.wav, sample.wav, voice.meta.json
  characters/<characterId>/   character.json   (references one avatarId and one voiceId)
  <legacyId>[.bundle]/        pre-2.0 inline character folder (read-only, still loads)
```

| Entity | What it is | Id | Cost |
|---|---|---|---|
| `Avatar` | A portrait plus the driving frames, latents and face crops LivePortrait and MuseTalk need to animate it. Immutable once built. | Content hash of the image bytes, the expression set (`CreationMode`), and `Avatar.Version`. The same portrait built the same way always has the same id, so `CreateAvatarAsync` is get-or-create. | Minutes and hundreds of MB the first time; seconds to load afterwards. |
| `Voice` | A saved speaker. `VoiceKind.Designed` (VoiceDesign checkpoint, from a description) or `VoiceKind.Cloned` (Base checkpoint, in-context from a reference recording and its transcript). Carries `Sample` / `SampleText`: the rendered take for a designed voice, the reference itself for a clone. | Designed: a fresh GUID per call — every design samples a new speaker, so two rolls are two voices. Cloned: content hash of the reference PCM and transcript, so cloning the same take twice loads the existing folder. | Seconds. |
| `Character` | A name plus references to one avatar and one voice. Nothing is copied. | GUID, assigned at creation, never changes. | Instant. |

What falls out of that:

- Several characters share one avatar folder; a new voice on the same face
  never re-runs the avatar pass.
- `Character.ReplaceVoice(voice)` is a one-line write to `character.json`.
- Speech audio is cached on **voice + text**, and lip-sync frames on
  **voice + text + avatar + expression** — never on the character.
- Deleting a character leaves its halves for the others. `DeleteAvatar` /
  `DeleteVoice` refuse (with the offending character ids) while something
  still references them.

## Install

Requires Unity **6000.0.46f1** or newer. Developed on macOS (CoreML); Windows
is untested.

Three steps, in this order, in `Packages/manifest.json` (or the equivalent
Package Manager UI):

1. **OpenUPM scoped registry** so `com.github.asus4.onnxruntime` resolves:

   ```json
   "scopedRegistries": [
     {
       "name": "OpenUPM",
       "url": "https://package.openupm.com",
       "scopes": [ "com.github.asus4" ]
     }
   ]
   ```

2. **Qwen3-TTS-Unity** from its git URL. LiveTalk's `package.json` declares
   `com.genesis.qwentts.unity` as a dependency by version, and Unity cannot
   resolve a dependency from a git URL by itself, so add it first:

   ```json
   "com.genesis.qwentts.unity": "https://github.com/arghyasur1991/Qwen3-TTS-Unity.git"
   ```

3. **This package**:

   ```json
   "com.genesis.livetalk.unity": "https://github.com/arghyasur1991/LiveTalk-Unity.git"
   ```

`com.github.asus4.onnxruntime`, `com.unity.nuget.newtonsoft-json` and
`com.unity.ugui` are pulled in as dependencies. Or clone both repositories
and add them with *Add package from disk…*.

The optional **LiveTalk Demo** sample (Package Manager → LiveTalk Unity →
Samples) is a single uGUI MonoBehaviour that runs every step below.

## Models

Weights are not included. Two sets are needed.

### LivePortrait and MuseTalk (ONNX)

Download the pre-exported ONNX models from
[Google Drive](https://drive.google.com/file/d/1UvssShqniAj_p-yw0dLDTWQEqe-O_n6K/view?usp=sharing),
extract, and place the `LiveTalk` folder under `Assets/Models/` so the layout is

```
Assets/Models/LiveTalk/models/
  LivePortrait/   appearance_feature_extractor, motion_extractor, stitching, stitching_eye,
                  stitching_lip, warping_spade_fp16, det_10g, 2d106det, landmark   (.onnx)
  MuseTalk/       unet_fp16, vae_encoder_fp16, vae_decoder_fp16, positional_encoding,
                  whisper_encoder, face_parsing                                   (.onnx)
```

Then open **LiveTalk → Model Deployment Tool** (a top-level editor menu). It
copies only the variants the runtime actually loads — FP16 for `warping_spade`
and the MuseTalk UNet/VAE on CoreML, FP32 for the rest — from `Assets/Models`
into `Assets/StreamingAssets/LiveTalk/models/`, with dry-run, overwrite and
backup options and a *Clean StreamingAssets* button. The runtime reads them
from `<parentModelPath>/LiveTalk/models/`, where `parentModelPath` defaults to
`Application.streamingAssetsPath`.

`MemoryUsage.Quality` loads FP32 variants of the FP16 models instead; those are
not deployed by the tool and must be placed manually.

### Qwen3-TTS (ONNX)

Two checkpoints, exported with the scripts in the Qwen3-TTS-Unity repository
(`Tools~/qwen3_tts_onnx/`) and installed anywhere you like:

| Checkpoint | Used for | Disk | Resident |
|---|---|---|---|
| `Qwen3-1.7B-VoiceDesign` | `DesignVoiceAsync` | ~8 GB | ~7 GB |
| `Qwen3-1.7B-Base` | `CloneVoiceAsync`, and every `SpeakAsync` of a cloned voice | ~8 GB | ~7 GB |

Point LiveTalk at the folder that contains both via `Initialize(ttsModelRoot:)`.
Left null, the TTS package looks in `StreamingAssets/QwenTTS`, which is fine in
the editor and usually wrong for a shipped player (StreamingAssets ships with
the build). **Window → Qwen3 TTS → Model Status** shows what was found. Read the
[Qwen3-TTS-Unity README](https://github.com/arghyasur1991/Qwen3-TTS-Unity#readme)
for the memory budget, reference-clip rules (≥ 4 s, 24 kHz, ≤ 20 s used) and
int8 precision.

## Initialize

`LiveTalkAPI` is a singleton. Call `Initialize` once; it builds the LivePortrait
and MuseTalk engines, initializes the TTS engine, sets up the cache and creates
a hidden `LiveTalkAPI` GameObject that hosts the coroutines.

```csharp
using LiveTalk.API;

LiveTalkAPI.Instance.Initialize(
    logLevel: LogLevel.INFO,                 // VERBOSE also opts into ONNX Runtime's own INFO/VERBOSE lines
    characterSaveLocation: "",               // "" → persistentDataPath/Characters   (avatars/, voices/, characters/)
    parentModelPath: "",                     // "" → Application.streamingAssetsPath (reads <path>/LiveTalk/models)
    memoryUsage: MemoryUsage.Balanced,       // see Memory
    cacheLocation: null,                     // null → persistentDataPath/LiveTalkCache
    enableCache: true,                       // false disables speech/frame caching entirely
    ttsModelRoot: null);                     // folder holding Qwen3-1.7B-VoiceDesign / Qwen3-1.7B-Base
```

| Parameter | Meaning |
|---|---|
| `logLevel` | `VERBOSE`, `INFO` (default), `WARNING`, `ERROR`. Applied to LiveTalk and the TTS package. ONNX Runtime's own log level follows: `VERBOSE` → verbose, `ERROR` → error, otherwise warning (ORT at info logs an arena line per allocation). |
| `characterSaveLocation` | Root of `avatars/`, `voices/`, `characters/`. Also readable as `LiveTalkAPI.CharacterSaveLocation`. |
| `parentModelPath` | Parent of the `LiveTalk/models/` tree the deployment tool writes. |
| `memoryUsage` | `Performance` (load every model at startup), `Balanced` (load on first use, keep — default), `Optimal` (load per use, dispose after; for constrained devices), `Quality` (FP32 everywhere; not recommended). Mapped onto the TTS package's policy too. |
| `cacheLocation` / `enableCache` | Where speech WAVs and lip-sync frame folders go. See [Caching](#caching). |
| `ttsModelRoot` | Passed straight to the TTS engine. |

Calling `Initialize` twice logs a warning and does nothing more — except
recreating the coroutine host if Play mode tore it down, which `EnsureRuntimeHost()`
also does on its own. In `MemoryUsage.Performance` mode,
`await LiveTalkAPI.Instance.WaitForAllModelsAsync(onProgress)` waits for the
LivePortrait and MuseTalk sessions (the TTS checkpoints are deliberately not
warmed there — see [Memory](#memory)).

Every long operation below is an `IEnumerator` with `onComplete` / `onError`
callbacks, meant to be driven with `yield return` from a coroutine (or
`StartCoroutine` from anywhere). Exactly one of the two callbacks fires.

## Create an avatar

```csharp
Avatar avatar = null;
yield return LiveTalkAPI.Instance.CreateAvatarAsync(
    portrait,                          // readable Texture2D
    CreationMode.AllExpressions,       // or SingleExpression (talk-neutral only), or VoiceOnly (image only, not animatable)
    onComplete: a => avatar = a,
    onError: ex => Debug.LogError(ex));
```

`CreateAvatarAsync` hashes the image and mode; if `avatars/<id>/` exists and is
complete it loads in seconds, otherwise LivePortrait animates the portrait with
the bundled driving clip for each expression and MuseTalk precomputes latents
and face crops. A run that fails removes its partial folder, so an avatar
folder is either complete or absent.

Expression indices, valid for `SpeakAsync` / `QueueSpeech` when the avatar has
them (`Avatar.ExpressionIndices`, `Avatar.CanAnimate`):

| Index | Expression | | Index | Expression |
|---|---|---|---|---|
| 0 | talk-neutral (also the idle loop) | | 4 | sad |
| 1 | approve | | 5 | surprised |
| 2 | disapprove | | 6 | confused |
| 3 | smile | | −1 | voice only, no frames |

Also: `LoadAvatarAsync(avatarId, onComplete, onError)`,
`GetAvailableAvatarIds()`, `DeleteAvatar(avatarId)`.

### How the driving clips are applied

Each expression is a bundled 25 fps clip of a rendered face
(`Resources/driving/*.mp4`). Rebuild them from scratch with
`python3 Tools~/driving_clips/build.py` (edit the SETTINGS block in that
file, then run; see `Tools~/driving_clips/README.md`). The clips
return to rest and share the lip-sync clock. Avatar creation renders one
LivePortrait frame per driving frame. A change to the clips or the crop
recipe bumps `Avatar.Version` and rebuilds.

## Design a voice (roll the dice)

```csharp
Voice voice = null;
yield return LiveTalkAPI.Instance.DesignVoiceAsync(
    Gender.Female, Pitch.Moderate, Speed.Moderate,
    instruct: "warm, unhurried, close-mic",          // optional free text, composed with the three enums
    sampleText: "Hello, this is a short voice sample.",
    onComplete: v => voice = v,
    onError: ex => Debug.LogError(ex));

// Audition the take. It is exactly what a clone would lock.
audioSource.clip = voice.Sample;
audioSource.Play();
```

Every call draws a **new speaker** and a new GUID; there is no seed. Keep the
one you like and `DeleteVoice(voice.Id)` the rest. `Voice.Sample` is
`sampleText` rendered at the engine's native 24 kHz, so it is a valid clone
reference as-is. The three enums are turned into a natural-language
description for the VoiceDesign checkpoint; `instruct` is appended.

## Clone a voice from a take

```csharp
Voice locked = null;
yield return LiveTalkAPI.Instance.CloneVoiceAsync(
    reference: voice.Sample,            // any AudioClip; ≥ 4 s at 24 kHz works best, first 20 s are used
    transcript: voice.SampleText,       // what the clip says — required for in-context cloning
    onComplete: v => locked = v,
    onError: ex => Debug.LogError(ex));
```

The id is a content hash of the reference PCM and transcript, so cloning the
same take again is a load, not another pass through the speaker and tokenizer
encoders. Without a transcript the clone falls back to a speaker-embedding
match: a stable voice, but not the one in your recording. The reference becomes
the clone's `Sample`, the transcript its `SampleText`.

Also: `LoadVoiceAsync(voiceId, onComplete, onError)`, `GetAvailableVoiceIds()`,
`DeleteVoice(voiceId)`.

## Create a character

```csharp
Character character = LiveTalkAPI.Instance.CreateCharacter("Mara", avatar, locked);
// character.Id, character.Name, character.Avatar, character.Voice, character.IsDataLoaded == true
```

Synchronous and instant: both halves already exist, so this writes
`characters/<guid>/character.json` and hands back a character that is loaded
and ready to speak. `avatar` may be null for a voice-only character.
`Character.SpeechSampleRate` is set to 16 kHz for an animatable avatar (what
the lip-sync stack consumes) and to the TTS model's native rate otherwise, and
is persisted.

```csharp
character.ReplaceVoice(anotherVoice);   // rewrites character.json; drops speech still queued in the old voice
```

Loading later:

```csharp
yield return LiveTalkAPI.Instance.LoadCharacterAsyncFromId(id, c => character = c, ex => …);
yield return LiveTalkAPI.Instance.LoadCharacterAsyncFromPath(folder, c => …, ex => …);
yield return LiveTalkAPI.Instance.LoadCharacterMetadataAsync(id, c => …, ex => …);   // name + image only, for lists
string[] ids = LiveTalkAPI.Instance.GetAvailableCharacterIds();                       // 2.0 folders plus legacy inline ones
LiveTalkAPI.Instance.DeleteCharacter(id);                                             // leaves avatar and voice in place
```

A load whose avatar or voice folder is missing fails through `onError`, naming
the missing half; it never returns a half-loaded character.

## Speak with CharacterPlayer

`CharacterPlayer` is a MonoBehaviour that plays the idle loop (expression 0,
25 fps ping-pong), queues speech, pipelines audio generation of the next line
with lip-sync of the current one, and raises events you can bind to any
display. Get one from the character (created lazily under a shared
`CharacterPlayers_Parent` GameObject) or add the component yourself and call
`AssignCharacter`.

```csharp
var player = character.CharacterPlayer;

player.OnFrameUpdate += frame => rawImage.texture = frame;   // idle and speech frames alike
player.OnReady        += () => Debug.Log("data and idle frames loaded");
player.OnSpeechStarted += () => Debug.Log("speaking");
player.OnSpeechEnded  += () => Debug.Log("nothing left to say");
player.OnError        += ex => Debug.LogError(ex);

// Safe to call immediately: lines queued before Ready are held and drain on OnReady.
player.QueueSpeech("Hello. I'm ready to talk.", expressionIndex: 0);
player.QueueSpeech("And this one smiles.",       expressionIndex: 3);
player.QueueSpeech("Narration, no face.",        withAnimation: false);

// Later
character.DestroyPlayer();   // Stop() + destroy the GameObject; the next CharacterPlayer access makes a new one
```

Text is split into sentences; each sentence is one segment. Audio for
segment *n+1* is generated while segment *n* animates.

### State machine

```
Uninitialized → Loading → Ready ⇄ Speaking
                                ↘ Paused ↗
```

| State | Meaning |
|---|---|
| `Uninitialized` | No character, or the assigned character failed to load. `QueueSpeech` is dropped with a warning. |
| `Loading` | Character data and/or idle frames loading. `QueueSpeech` enqueues; the queue drains right after `OnReady`. |
| `Ready` | Data **and** idle frames loaded, idle loop playing, nothing queued or in flight. `OnReady` then `OnCharacterLoaded` fire on entry. (`PlaybackState.Idle` is an obsolete alias with the same value.) |
| `Speaking` | At least one line is being generated or played. Stays `Speaking` across consecutive lines, including lines queued during the last segment — no intermediate `OnSpeechEnded`. |
| `Paused` | `Pause()` stops the idle loop and pauses the audio. `Resume()` returns to `Speaking` if that was the paused state, else to `Ready`, then starts anything queued meanwhile. |

- `IsPlaying` is true **only** while `Speaking`. For "finished", test
  `!player.IsPlaying && player.QueuedSpeechCount == 0` or listen for
  `OnSpeechEnded`. `IsReady` is `State == Ready`.
- `Stop()` stops the idle loop, the segment player, every in-flight frame
  collector and the audio source; clears the speech and pending queues; resets
  the pause bookkeeping; and returns to `Ready` (with idle) if the character is
  loaded. It does **not** abort a character load in progress (loading is not
  playback), and it does **not** kill a line whose TTS synthesis is mid-flight:
  the speech processor is retired and lets that synthesis finish on its normal
  path — which is where the voice lease is released — then discards the result.
- `ClearQueue()` drops queued lines without stopping the current one.
- `withAnimation: false`, or a character with no animatable avatar, plays audio
  only; the static portrait is shown if there is one.

### Streaming lip-sync (experimental, off by default)

By default a line is fully synthesised and fully animated before the first
frame is shown. `LiveTalkAPI.Instance.StreamLipSync = true` instead feeds
each TTS chunk to an incremental feature extractor and animates frames as
their audio window becomes final, so playback starts after roughly the first
half second of audio (`CharacterPlayer.PrerollSeconds`, default 0.35 s, is the
audio buffered before playback begins; `StreamLipSyncContextSeconds`, default
0.5 s, is extra audio held back per frame for the encoder's context).

Why it is off: measured against the batch path, streamed frames differ in
the mouth region by a mean of about 0.5–1.0/255 (max ~4.8/255). The residual
is the Whisper encoder's global attention seeing a prefix instead of the
whole clip, so it does not close with more context. And unless the GPU can
generate a frame in under 40 ms, playback holds the last frame while
generation catches up. Time to first mouth movement drops from ~29 s to
~2.4 s on a 4 s line. Turn it on when latency matters more than fidelity;
the batch path is unchanged when it is off, and cache hits are unaffected
either way.

## Speak directly with SpeakAsync

`Character.SpeakAsync` is the primitive the player is built on: one utterance,
audio first, frames streamed after.

```csharp
yield return character.SpeakAsync(
    "Where were you last night?",
    expressionIndex: 5,
    onAudioReady: (frames, clip) =>
    {
        audioSource.clip = clip;               // schedule the next SpeakAsync here to pipeline
        audioSource.Play();
        StartCoroutine(Drain(frames));
    },
    onAnimationComplete: frames => Debug.Log($"{frames.TotalExpectedFrames} frames"),
    onError: ex => Debug.LogError(ex),
    onSpeechChunk: (pcm, sampleRate) => { /* optional: audio as it is generated, main thread, ~1 s to first chunk */ });

IEnumerator Drain(FrameStream frames)
{
    while (frames.HasMoreFrames)
    {
        var next = frames.WaitForNext();
        yield return next;
        if (next.Texture != null) rawImage.texture = next.Texture;
    }
    if (frames.Error != null) Debug.LogError(frames.Error);   // a faulted producer still finishes the stream
}
```

Voice only — no avatar needed, native sample rate, `onAnimationComplete` fires
immediately after `onAudioReady` with an empty stream:

```csharp
yield return character.SpeakAsync("Voice only.", expressionIndex: -1,
    onAudioReady: (_, clip) => { audioSource.clip = clip; audioSource.Play(); },
    onError: ex => Debug.LogError(ex));
```

`onSpeechChunk` delivers only samples not reported before, so appending chunks
in order reproduces the utterance; it is skipped on a cache hit, where the whole
clip is already on disk. TTS requests are serialised through one voice queue and
MuseTalk requests through another, so concurrent `SpeakAsync` calls are safe
and simply take turns.

To roll a new take of the same voice and text without turning the whole cache
off, pass `useCache: false`. That call skips the audio cache **read**,
synthesises, and still **writes** the new wav so the next default
`SpeakAsync` hits it. Lip-sync frames include that wav's content hash, so
a later animated speak of the new take misses mouths generated against the
old wav — the host does not delete folders. Voice-only (`expressionIndex:
-1`) never runs MuseTalk. `LiveTalkAPI.SetCacheEnabled(false)` is a global
switch, not a per-utterance skip.

## DialogueOrchestrator

Turn-based multi-character dialogue over several `CharacterPlayer`s: it
switches the active speaker, stops the previous one, forwards the current
speaker's frames, and waits for each line to finish before the next.

```csharp
var orchestrator = new GameObject("Dialogue").AddComponent<DialogueOrchestrator>();
orchestrator.RegisterCharacter("mara", mara.CharacterPlayer);
orchestrator.RegisterCharacter("tom",  tom.CharacterPlayer);
orchestrator.RegisterCharacter("narrator", narrator.CharacterPlayer);   // voice-only character is fine

orchestrator.OnFrameUpdate    += frame => rawImage.texture = frame;
orchestrator.OnSpeakerChanged += id => nameLabel.text = id;
orchestrator.OnDialogueEnded  += () => Debug.Log("scene over");

orchestrator.QueueDialogue("mara", "Where were you last night?", expressionIndex: 0);
orchestrator.QueueDialogue("tom",  "At home. I swear.",           expressionIndex: 5);
orchestrator.QueueDialogue("narrator", "He was not at home.", withAnimation: false);

orchestrator.QueueDialogueBatch(new List<DialogueOrchestrator.DialogueSegment>
{
    new() { CharacterId = "mara", Text = "Then explain this.", ExpressionIndex = 2 },
});
```

Also: `UnregisterCharacter(id)`, `Stop()`, `ClearQueue()`, `IsPlaying`,
`QueuedDialogueCount`, `CurrentSpeakerId`, `OnDialogueStarted`, `OnError`.

## Performances (scripted scenes)

`CharacterPlayer` and `DialogueOrchestrator` are for conversations where the
next line is not known: one utterance at a time, the face idles between them,
each line carries its own expression. A **scripted scene** is the opposite —
everything is known before the first frame — and wants two independent tracks
on one clock:

- an **expression track**: what the face does over time, regardless of speech —
  play an expression clip through, or play to its peak and *hold* it with the
  idle clip's own micro-motion; blend into the next; react while someone else
  is talking;
- a **speech track**: timed utterances by any character, lip-synced onto
  *whatever the expression track has at that tick*, overlapping across
  characters when a line interrupts another.

```csharp
var perf = new Performance { DefaultGap = 0.3f };

// Lines chain after the previous one by default (any character).
var l1 = perf.AddUtterance(alex, "That's because it bends it.");
var l2 = perf.AddUtterance(you,  "…It bends time.");                  // voice-only character
var l3 = perf.AddUtterance(alex, "I've been saving that for three weeks.",
                           Anchor.End(l2, -0.2f));                    // steps on the end of l2

// Expressions are placed independently of speech.
var smirk = perf.AddExpression(alex, 3 /* smile */, Anchor.Start(l1, -0.3f));   // face leads the line
smirk.Mode = ExpressionMode.HoldAtPeak; smirk.Peak01 = 0.6f; smirk.HoldSeconds = 2f; smirk.Micro01 = 0.15f;
perf.AddExpression(alex, 6 /* confused */, Anchor.Start(l2, 0.4f));          // reacts while `you` talk

yield return api.RenderPerformanceAsync(perf,
    rendered =>
    {
        var player = api.CreatePerformancePlayer(rendered);
        player.OnFrame   += (characterId, tex) => { if (characterId == alex.Id) rawImage.texture = tex; };
        player.OnCaption += c => caption.text = c.Text;
        player.OnReady   += () => player.Play();
    },
    onError: ex => Debug.LogError(ex),
    onProgress: (stage, p) => Debug.Log($"{p:P0} {stage}"));
```

What rendering does, once per fingerprint (cues + characters + voices):
audio is synthesised (or loaded from the speech cache) **without playing
it** — `SpeakAsync` with `expressionIndex: -1` only produces a clip.
`IsPerformanceRendered` is true once the folder exists, so a host can
preview those clips itself on the first bake. `CreatePerformancePlayer`
is what actually plays wavs and frames.

What rendering does, once per fingerprint (cues + characters + voices):

1. **Audio** for every utterance (the normal speech cache, voice + text).
2. **Resolve**: anchors become seconds; the expression track becomes a *pose
   per tick* for each animated character. Idle (expression 0) runs underneath,
   forward, wrapping. A cue blends in from whatever pose is on screen, plays its
   clip (or plays to the peak and holds), and blends back out. A tick that lands
   exactly on a stored driving frame is that frame — free. Blends and holds are
   rendered from the avatar's recorded poses (`motion.bin`, avatar v2) into a
   pose cache, once ever per avatar + pose.
3. **Lip-sync** per spoken utterance over the base frames its ticks have.
   Mouths are cached on `hash(voice, text, avatar, planSlice)` — the
   sequence of base faces under the line, not the performance clock.
   Nudging a cue that does not change that sequence reuses the mouths;
   a re-timed expression track under the line misses and re-inpaints.
   Chat lip-sync (`CharacterPlayer`) still keys on expression index.
4. A **manifest** (`perf_<fingerprint>/performance.json` under the cache) with a
   frame path per tick per character, the wavs, and captions. Frames are
   referenced, not copied (avatar PNGs, pose cache, mouth cache).

`PerformancePlayer` streams frames from disk `Lookahead` ticks ahead of the play
head, plays each utterance's wav on a per-character `AudioSource` child, and
raises `OnFrame(characterId, texture)`, `OnUtteranceStarted`, `OnCaption` /
`OnCaptionCleared`, `OnEnded`. Transport: `Play`, `Pause`, `Resume`, `Stop`,
`Seek(seconds)`, `Time`, `PlaybackState`.

`Anchor.At(seconds)`, `Anchor.Start(cue, offset)`, `Anchor.End(cue, offset)`,
`Anchor.AfterPrevious(offset)`. Two utterances of the *same* character never
overlap (the later one is pushed); different characters may. An `Utterance`
with `LipSync = false` plays its audio over the face as it is (a vocalisation,
or a character with no avatar).

`api.RenderPosesAsync(portrait, poses, onFrame)` is the primitive underneath:
frames from 63-float driving poses, no extractor. Poses come from
`Avatar` frames (recorded at build) or any interpolation / offset of them.

## Raw animation without a character

The two engines are also exposed directly; each returns a `FrameStream`.

```csharp
// LivePortrait: transfer motion from driving frames onto a portrait
FrameStream a = api.GenerateAnimatedTexturesAsync(portrait, drivingFrames /* List<Texture2D> */);
FrameStream b = api.GenerateAnimatedTexturesAsync(portrait, videoPlayer, maxFrames: -1);
FrameStream c = api.GenerateAnimatedTexturesAsync(portrait, "path/to/frames", maxFrames: 50);

// MuseTalk: lip-sync a portrait (plus optional extra frames in a folder) to a clip
FrameStream d = api.GenerateTalkingHeadAsync(portrait, "path/to/avatar/frames", audioClip);
```

`FrameStream`: `TotalExpectedFrames`, `HasMoreFrames`, `Error`,
`WaitForNext()` (yieldable; read `.Texture`), `TryGetNext(out texture)`.

## Caching

With caching on (the default), `SpeakAsync` — and therefore `CharacterPlayer`
and `DialogueOrchestrator` — read and write two kinds of entry under
`cacheLocation` (default `persistentDataPath/LiveTalkCache`):

| Entry | Key | On disk |
|---|---|---|
| Speech audio | `hash(voiceId, text)` | `<key>.wav` |
| Lip-sync frames (chat) | `hash(voiceId, text, avatarId, expressionIndex, wavHash)` | `<key>_frames/frame_000000.png …` |
| Lip-sync frames (performances) | `hash(voiceId, text, avatarId, planSlice, wavHash)` | `<key>_frames/frame_000000.png …` |
| Rendered pose (performances) | `hash(avatarId, pose)` | `pose_<key>.png` |
| Rendered performance | performance fingerprint | `perf_<key>/performance.json` |

Because the key is the voice, not the character, two characters sharing a voice
share the audio, a replaced voice never replays old takes, and the same line at
two expressions never shares frames. Lip-sync frames also hash the wav
bytes: a re-rolled take misses mouths generated against the previous wav
(`frames_cache_v3` / `perf_mouth_v2`; old folders are simply never matched).
A frames folder left short by a failed run is deleted rather than taken as
a hit next time. `SpeakAsync(..., useCache: false)` and
`QueueSpeech(..., useCache: false)` skip the audio read for that call only
and overwrite the matching wav.

Avatars, voices and characters are **not** cache and live under the save
location; the avatar folder is its own cache (asking for the same portrait
again loads instead of rebuilding).

```csharp
LiveTalkAPI.CacheLocation;                 // the folder in use
LiveTalkAPI.IsCacheEnabled;
LiveTalkAPI.SetCacheEnabled(false);        // runtime toggle
long bytes = LiveTalkAPI.GetCacheSizeBytes();      // walks the folder — not per frame
LiveTalkAPI.ClearCache();

// Both also take an explicit folder and then work BEFORE Initialize, so a
// settings screen can show and clear the cache without paying for model setup:
LiveTalkAPI.GetCacheSizeBytes(Path.Combine(Application.persistentDataPath, "LiveTalkCache"));
LiveTalkAPI.ClearCache(Path.Combine(Application.persistentDataPath, "LiveTalkCache"));
```

Entries written by 1.x (speech keyed on text + character id, `vs_*` voice-style
folders, `df_*` driving-frame copies) use different key salts and are simply
never matched again; `ClearCache` removes them.

## Error handling contract

- Every `IEnumerator` API with `onComplete` / `onError` fires **exactly one** of
  them. `onComplete` is never called with a half-built or half-loaded object: a
  missing model file, a clone the engine could not build, a voice folder that
  did not load, an avatar expression without its latents — all reach `onError`
  with the original exception (and the offending path where there is one).
- A failed `CreateAvatarAsync` / `DesignVoiceAsync` / `CloneVoiceAsync` removes
  its partial folder. Each writes into a `<id>.partial-…` staging folder that no
  id lookup matches and moves it into place only when complete; leftovers from a
  crash are swept on `Initialize`.
- `SpeakAsync`: a failure after audio was handed to `onAudioReady` still fires
  `onError`, never `onAnimationComplete`, and the `FrameStream` is finished with
  `FrameStream.Error` set so a consumer draining it exits. `HasMoreFrames` alone
  cannot tell a short clip from a failed one; check `Error`.
- Synchronous calls (`CreateCharacter`, `ReplaceVoice`, `DeleteAvatar`,
  `DeleteVoice`, `DeleteCharacter`) throw.
- `CharacterPlayer.OnError` / `DialogueOrchestrator.OnError` relay failures of
  queued lines; a line that fails is skipped and the queue continues.
- Internally, every wait on a `Task` observes `IsFaulted`, every frame producer
  marks its stream finished in `finally`, and every queue lease is released in
  `finally`, so a fault cannot leave a consumer waiting forever or a lock held.

## Memory

| `MemoryUsage` | LivePortrait / MuseTalk | TTS |
|---|---|---|
| `Performance` | All sessions opened at `Initialize`; `WaitForAllModelsAsync` waits for them | Load eagerly, never drop |
| `Balanced` (default) | Open on first use, keep | Load on first use, keep |
| `Optimal` | Open per use, dispose after | Load per use, dispose after (idle ≈ embedding tables) |
| `Quality` | FP32 variants, opened at startup | As `Performance` |

The TTS checkpoints are the big number: ~7 GB resident each, and they are wanted
in **different phases** — VoiceDesign while a voice is being chosen, Base for
cloning and everything after. Load and drop them explicitly rather than holding
both (these two take `QwenTTS.QwenCheckpoint`, so add `using QwenTTS;`):

```csharp
await LiveTalkAPI.WarmUpVoiceAsync(QwenCheckpoint.VoiceDesign);   // from a loading screen; ~10 s cold
// … design, audition, pick …
LiveTalkAPI.EvictVoice(QwenCheckpoint.VoiceDesign);               // memory back
await LiveTalkAPI.WarmUpVoiceAsync(QwenCheckpoint.Base);

LiveTalkAPI.Instance.UnloadTts();      // drop every TTS session; LivePortrait / MuseTalk stay
LiveTalkAPI.VoiceModelsLoaded;         // true while either checkpoint is resident
LiveTalkAPI.Instance.Dispose();        // dispose the inference engines (main thread; there is no finalizer)
```

Avatar building is the other peak: an `AllExpressions` avatar keeps the
generated frames in memory while MuseTalk precomputes latents unless
`MemoryUsage.Optimal`, which streams them through disk. 32 GB is comfortable
for creation; 16 GB works with one TTS checkpoint resident and `Optimal`.

## Migrating from 1.x

2.0 is a breaking release: the TTS backend changed (Spark-TTS → Qwen3-TTS, so
1.x voice folders cannot be reused), the character folder format changed from
copies to references, and the cache keys changed.

| 1.x | 2.0 |
|---|---|
| `CreateCharacterAsync(name, gender, image, pitch, speed, intro, voicePromptPath, onComplete, onError, …)` | `[Obsolete]`, still works: forwards to `CreateAvatarAsync` + (`CloneVoiceAsync` when `voicePromptPath` is given, else `DesignVoiceAsync` with `intro` as the sample text) + `CreateCharacter`, and produces a 2.0 reference character with a GUID id. `useBundle` is ignored. Call the three directly. |
| `GenerateVoicePreviewAsync` → `VoicePreviewResult` | `[Obsolete]`; the preview is now a saved `Voice` (`VoicePreviewResult.Voice`, `VoiceFolderPath` = its folder). Use `DesignVoiceAsync`; discard with `DeleteVoice`. `CleanupVoicePreviews` / `DeleteVoicePreview` likewise. |
| `Character.Gender` / `Pitch` / `Speed` / `Intro` / `VoiceInstruct` / `VoiceCloneRefText` / `VoicePromptClip` | `[Obsolete]` forwards to `Character.Voice.Gender` / `Pitch` / `Speed` / `SampleText` / `Instruct` / `SampleText` / `Sample`. |
| `Character.CharacterId` settable | Read-only. `Character.Id` is the same value, a GUID assigned at creation. |
| `Character.CharacterPlayer` created eagerly on load | Created lazily on first access; `DestroyPlayer()` tears it down. |
| `PlaybackState.Idle` | `PlaybackState.Ready` (same value; `Idle` is an `[Obsolete]` alias). `Ready` also means idle frames are loaded. |
| `CharacterPlayer.IsPlaying` true while Idle or Speaking | True only while `Speaking`. `DialogueOrchestrator` relies on this; if you polled `IsPlaying` as "loaded", use `IsReady` / `OnReady`. |
| `QueueSpeech` before load: dropped with a warning | Enqueued; drains on `OnReady`. |
| Inline character folders `<saveLocation>/<id>[.bundle]/` with `image.png`, `drivingFrames/`, `voice/` | Still load via `LoadCharacterAsyncFromId` / `LoadCharacterMetadataAsync` / `GetAvailableCharacterIds` (`Character.IsLegacy`, logged once), **read-only**: their voice cannot be replaced and their halves cannot be shared or deleted on their own. Recreate through the 2.0 API to migrate. The 2.0 layout never writes `.bundle` folders (`CanUseBundle()` only says whether legacy ones can be read). |
| Speech cache keyed on text + character id, no expression | Keyed on voice + text (+ avatar + expression for frames). Old entries are ignored; `ClearCache` reclaims them. `vs_*` / `df_*` folders are gone. |
| `Character.CreateAvatarAsync(...)` instance method | Removed. Use `LiveTalkAPI.CreateAvatarAsync`. |
| `LiveTalkAPI.WaitForAllModelsAsync` warmed the TTS | Waits for LivePortrait / MuseTalk only. Use `WarmUpVoiceAsync`. |

Removed without replacement: `StartSpeakWithCallbacks` (the 1.x README described
it, but `SpeakAsync` with `onAudioReady` / `onAnimationComplete` is and was the
method), `QueueSpeechBatch`, `HasQueuedSpeech` (use `QueuedSpeechCount`).

## Requirements and performance

- Unity 6000.0.46f1 or newer.
- macOS with CoreML tested (Apple silicon). Windows compiles, untested.
- RAM: 32 GB recommended for avatar creation with a TTS checkpoint resident;
  see [Memory](#memory).
- Disk: ~7 GB LivePortrait + MuseTalk ONNX, plus ~8 GB per Qwen3-TTS checkpoint.

Measured on a MacBook Pro M4 Max, ONNX Runtime with the CoreML execution
provider:

| Stage | |
|---|---|
| Speech with lip-sync | 10–11 fps generated |
| Avatar creation, `SingleExpression` | ~2 minutes |
| Avatar creation, `AllExpressions` | ~10 minutes |
| LivePortrait pipeline | ~4 fps (`motion_extractor` 30–60 ms, `warping_spade` fp16 180–250 ms, `landmark` 2–3 ms) |
| MuseTalk pipeline | 11–12 fps (`vae_encoder` 20–30 ms, `unet` 30–40 ms, `vae_decoder` 30–50 ms) |
| TTS | ~0.97× real time; first streamed chunk in ~1 s; ~11 s cold session open per checkpoint |

## License

This package is licensed under the [MIT License](LICENSE).

It builds on, and its model exports derive from:

- [LivePortrait](https://github.com/KwaiVGI/LivePortrait) — MIT License
- [MuseTalk](https://github.com/TMElyralab/MuseTalk) — MIT License
- [Qwen3-TTS-Unity](https://github.com/arghyasur1991/Qwen3-TTS-Unity) — Apache License 2.0.
  The Qwen3-TTS weights are Alibaba's, released under Apache-2.0 and not part
  of either package; check the model cards before shipping:
  [Qwen3-TTS-12Hz-1.7B-VoiceDesign](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign),
  [Qwen3-TTS-12Hz-1.7B-Base](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base).
- [ONNX Runtime](https://github.com/microsoft/onnxruntime) and
  [onnxruntime-unity](https://github.com/asus4/onnxruntime-unity) — MIT License

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
