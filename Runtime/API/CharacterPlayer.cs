using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace LiveTalk.API
{
    using Utils;
    /// <summary>
    /// Playback state of a <see cref="CharacterPlayer"/>.
    ///
    /// <code>
    /// Uninitialized → Loading → Ready ⇄ Speaking
    ///                                 ↘ Paused ↗
    /// </code>
    ///
    /// <list type="bullet">
    /// <item><b>Uninitialized</b> — no character, or the assigned character failed to load.</item>
    /// <item><b>Loading</b> — character data and/or idle frames are being loaded. Speech queued now is held and drained on <see cref="CharacterPlayer.OnReady"/>.</item>
    /// <item><b>Ready</b> — character data <i>and</i> idle frames are loaded; idle animation is playing (when enabled) and nothing is queued or in flight. Formerly named <see cref="Idle"/>.</item>
    /// <item><b>Speaking</b> — at least one line is being generated or played. The player stays here across consecutive lines, including lines queued while the last segment was playing.</item>
    /// <item><b>Paused</b> — <see cref="CharacterPlayer.Pause"/> was called. Whether <see cref="CharacterPlayer.Resume"/> returns to Ready or Speaking depends on which state was paused.</item>
    /// </list>
    /// </summary>
    public enum PlaybackState
    {
        Uninitialized,
        Loading,
        Ready,
        Speaking,
        Paused,

        /// <summary>
        /// Same value as <see cref="Ready"/>; kept so existing comparisons compile.
        /// The name was misleading — it read as "idle animation is playing", but it
        /// always meant "loaded and not speaking".
        /// </summary>
        [Obsolete("Use PlaybackState.Ready. Idle and Ready are the same value.")]
        Idle = Ready
    }

    /// <summary>
    /// Speech request data structure
    /// </summary>
    internal class SpeechRequest
    {
        public List<string> TextLines { get; set; } = new List<string>();
        public int ExpressionIndex { get; set; }
        public bool WithAnimation { get; set; } = true;
        public bool UseCache { get; set; } = true;
    }

    /// <summary>
    /// CharacterPlayer is a reusable MonoBehaviour component that handles character loading,
    /// idle animation playback, and speech animation with smooth transitions.
    /// 
    /// Usage:
    /// 1. Add to GameObject with RawImage component
    /// 2. Assign a LiveTalk Character
    /// 3. Call QueueSpeech() to make character speak
    /// 
    /// Features:
    /// - Auto-loads character when assigned
    /// - Plays idle animation (expression 0) as a forward loop at the avatar's
    ///   frame rate (bundled clips are rest-to-rest)
    /// - Queues and plays speech with smooth transitions
    /// - Seamlessly returns to idle after speech
    /// - Speech may be queued at any time after <see cref="AssignCharacter"/>; lines queued
    ///   before the player is <see cref="PlaybackState.Ready"/> play as soon as it is.
    ///
    /// <para><b>Continuity.</b> Every frame on screen is some avatar frame:
    /// idle frame <c>k</c> is avatar frame <c>k</c>, and lip-sync frame
    /// <c>i</c> of a line started on avatar frame <c>s</c> is avatar frame
    /// <c>s + i</c>. The player keeps one cursor across both. When a line is
    /// generated the player predicts where the idle loop will be when it can
    /// start playing and asks for the frames to be rendered from there; when
    /// the line is ready it lets the idle run on to that frame (bounded by
    /// <see cref="MaxContinuityWaitSeconds"/>) so the head never jumps, and on
    /// the way out idle resumes from the frame speech ended on.</para>
    /// </summary>
    public class CharacterPlayer : MonoBehaviour
    {
        // Inspector-assignable
        [SerializeField] private Texture _displayImage = null;
        [SerializeField] private readonly bool autoPlayIdle = true;
        /// <summary>Idle rate used only when the character reports none (a legacy avatar).</summary>
        [SerializeField] private readonly float idleFPS = 25f;
        [SerializeField] private bool audioOnly = false; // For characters without avatars
        
        // Runtime state
        private Character _character;
        public Texture DisplayImage 
        { 
            get 
            { 
                return _displayImage;
            } 
            private set 
            { 
                if (_displayImage != value) 
                {
                    _displayImage = value;
                    OnFrameUpdate?.Invoke(_displayImage);
                }
            }
        }
        private PlaybackState _state = PlaybackState.Uninitialized;
        private readonly Queue<SpeechRequest> _speechQueue = new();

        /// <summary>
        /// Bumped by <see cref="Stop"/>. Every long-running coroutine captures the
        /// value it started under and stops touching player state once it no
        /// longer matches. This is what lets <see cref="Stop"/> retire the speech
        /// processor without killing it mid-<c>SpeakAsync</c> (see the comment in
        /// <see cref="Stop"/> for why that matters).
        /// </summary>
        private int _epoch;

        // Loading
        private Coroutine _loadCoroutine;
        private bool _idleLoaded;
        
        // Idle animation. _idleFrameIndex is the avatar-frame cursor: the next
        // idle frame to show, kept current through speech as well (see the
        // class remarks).
        private List<Texture> _idleFrames;
        private int _idleFrameIndex = 0;
        private Coroutine _idleCoroutine;
        private float _idleNextFrameTime;
        private int _shownAvatarIndex = -1;

        // Continuity estimators: how long lip-sync generation takes from the
        // moment the start frame is chosen to the moment the line can play.
        // Batch: per frame; streaming: a flat lead. Learnt from every line.
        private float _batchSecondsPerFrame = 0.15f;
        private float _streamLeadSeconds = 0.8f;
        private const float EstimatorGain = 0.4f;
        
        // Speech animation
        private Coroutine _speechCoroutine;
        private Coroutine _animationCoroutine;
        private AudioSource _audioSource;

        // Pause bookkeeping
        private bool _pausedWhileSpeaking;
        private bool _idleWasRunningAtPause;
        private bool _destroyed;
        
        // Pipelined processing
        private readonly Queue<PendingSpeechItem> _pendingAnimations = new();
        private PendingSpeechItem _playingItem;
        private readonly List<FrameCollector> _frameCollectors = new();
        private bool _isSpeechProcessorRunning = false;
        private bool _isAnimationPlayerRunning = false;
        
        private class PendingSpeechItem
        {
            public List<Texture> Frames { get; set; }
            public AudioClip AudioClip { get; set; }
            public bool AudioReady { get; set; }
            public bool AnimationReady { get; set; }
            public FrameStream FrameStream { get; set; }
            public bool WithAnimation { get; set; } = true;

            /// <summary>Set when the line's lip-sync is streamed: audio and frames arrive while it plays.</summary>
            public SpeechStream Stream { get; set; }

            /// <summary>A <see cref="CollectAnimationFrames"/> is draining <see cref="FrameStream"/> into <see cref="Frames"/>.</summary>
            public bool CollectorStarted { get; set; }

            /// <summary>Avatar frame the generator was asked to start on, or -1 if never asked (audio-only, legacy).</summary>
            public int PredictedStart { get; set; } = -1;

            /// <summary>When the start frame was chosen (generation about to begin), for the latency estimators.</summary>
            public float GenerationStartedAt { get; set; }

            /// <summary>Frames the line was expected to have when the start was chosen; -1 when streaming.</summary>
            public int GenerationFrames { get; set; } = -1;

            /// <summary>Avatar frame the frames actually start on, read off the stream once the item is ready; -1 unknown.</summary>
            public int StartFrameIndex =>
                Stream?.Frames?.StartFrameIndex ?? FrameStream?.StartFrameIndex ?? -1;

            /// <summary>Avatar frame the line ends on (exclusive), or -1 unknown. Used to chain the next line's start.</summary>
            public int ExpectedEnd(int loopLength)
            {
                int start = StartFrameIndex >= 0 ? StartFrameIndex : PredictedStart;
                if (start < 0 || loopLength <= 0)
                    return -1;
                int frames = FrameStream != null && FrameStream.TotalExpectedFrames > 0
                    ? FrameStream.TotalExpectedFrames
                    : GenerationFrames;
                if (frames <= 0)
                    return -1;
                return (start + frames) % loopLength;
            }
        }

        // Streaming playback: one streaming AudioClip whose PCM reader pulls
        // from the line's SpeechStream on the audio thread.
        private AudioClip _streamClip;
        private volatile SpeechStream _streamSource;
        private volatile int _streamReadPos;
        private volatile int _streamMaxBlock;
        private int _streamUnderruns;
        private const int MaxStreamedSeconds = 600;

        // OnSpeechStarted bookkeeping: fired on the first displayed speech
        // frame of a Speaking run, not when the run is queued.
        private bool _speechRunStarted;
        private float _speechQueuedAt;

        /// <summary>
        /// Audio buffered before a streamed line starts playing, in seconds.
        /// Streamed lip-sync (<see cref="LiveTalkAPI.StreamLipSync"/>) begins
        /// once the first frames exist <i>and</i> this much audio is in hand.
        /// Larger values start later but ride out a slow synthesis chunk
        /// without the audio pausing to wait for it; smaller values start
        /// sooner. Speech is generated slightly faster than real time, so
        /// 0.35 s is normally enough. Not used by the batch path or cache hits.
        /// </summary>
        public float PrerollSeconds { get; set; } = 0.35f;

        /// <summary>
        /// Render each line's lip-sync frames from the avatar frame the idle
        /// loop will be on when the line starts, and let the idle run on to
        /// that frame before switching, so speech continues the idle motion
        /// instead of jumping to the clip's first frame. Off, every line starts
        /// from avatar frame 0. Default on.
        /// </summary>
        public bool SpeechContinuity { get; set; } = true;

        /// <summary>
        /// Longest the player will let the idle loop run on to reach the frame
        /// a ready line starts on, in seconds. The start frame is predicted
        /// when generation begins; if the idle has since gone past it, waiting
        /// means going round the loop again, and past this bound the player
        /// cuts instead (logged with the size of the jump). Default 2 s.
        /// </summary>
        public float MaxContinuityWaitSeconds { get; set; } = 2f;

        /// <summary>
        /// Margin added to the predicted generation time when choosing a
        /// line's start frame, in seconds. Erring late costs a short idle
        /// run-on before the line; erring early costs a cut (or a wait round
        /// the loop). Default 0.25 s.
        /// </summary>
        public float ContinuityLeadSeconds { get; set; } = 0.25f;

        /// <summary>
        /// The avatar frame currently on screen — an index into the
        /// character's idle frames (expression 0), whether the frame came
        /// from the idle loop or from a line's lip-sync — or -1 when nothing
        /// animated is showing.
        /// </summary>
        public int CurrentAvatarFrameIndex => _shownAvatarIndex;

        /// <summary>Frames in the idle loop, or 0 before they are loaded.</summary>
        public int IdleFrameCount => _idleFrames?.Count ?? 0;

        /// <summary>Continuity applies: the feature on, and frames to walk.</summary>
        private bool ContinuityActive => SpeechContinuity && !audioOnly && _idleFrames != null && _idleFrames.Count > 1;

        /// <summary>The idle frame rate: the avatar's, or the serialized fallback for a legacy avatar.</summary>
        private float IdleFps
        {
            get
            {
                float fps = _character != null ? _character.IdleFrameRate : 0f;
                return fps > 0f ? fps : (idleFPS > 0f ? idleFPS : 25f);
            }
        }

        /// <summary>One in-flight <see cref="CollectAnimationFrames"/>, so <see cref="Stop"/> can find it.</summary>
        private class FrameCollector
        {
            public Coroutine Handle;
        }
        
        // Events
        public event Action<Texture> OnFrameUpdate;
        public event Action OnSpeechStarted;
        public event Action OnSpeechEnded;
        public event Action<Exception> OnError;
        public event Action OnCharacterLoaded;
        public event Action OnIdleStarted;

        /// <summary>
        /// Raised once the assigned character's data <i>and</i> its idle frames are
        /// loaded — the moment the player enters <see cref="PlaybackState.Ready"/>
        /// (or would have, had speech not already been queued: any lines queued
        /// while loading start draining immediately after this event, so a host
        /// does not need to wait for it before calling <see cref="QueueSpeech"/>).
        /// <see cref="OnCharacterLoaded"/> fires right after this, for compatibility.
        /// </summary>
        public event Action OnReady;
        
        // Public properties
        public PlaybackState State => _state;
        public Character Character => _character;

        private static Transform s_parentTransform;

        /// <summary>
        /// Shared scene parent for player GameObjects. Cached after the first
        /// lookup; re-found (or re-created) only if the cached object has been
        /// destroyed, e.g. by a scene change.
        /// </summary>
        public static Transform ParentTransform {
            get {
                // UnityEngine.Object's == null is true for a destroyed object too.
                if (s_parentTransform == null)
                {
                    GameObject parent = GameObject.Find("CharacterPlayers_Parent");
                    if (parent == null)
                    {
                        parent = new GameObject("CharacterPlayers_Parent");
                        parent.transform.SetParent(null); // Keep at root for persistence
                    }
                    s_parentTransform = parent.transform;
                }
                return s_parentTransform;
            }
        }

        /// <summary>
        /// True only while the player is <see cref="PlaybackState.Speaking"/>: a
        /// line is being generated or played. False while Ready (idle animation
        /// does not count as playing), Loading, Paused or Uninitialized. A host
        /// that wants "nothing left to say" should test
        /// <c>!IsPlaying &amp;&amp; QueuedSpeechCount == 0</c>, or listen for
        /// <see cref="OnSpeechEnded"/>, which fires exactly when that becomes true.
        /// </summary>
        public bool IsPlaying => _state == PlaybackState.Speaking;

        /// <summary>
        /// True when character data and idle frames are loaded and nothing is
        /// speaking, queued or paused — i.e. <see cref="State"/> is
        /// <see cref="PlaybackState.Ready"/>.
        /// </summary>
        public bool IsReady => _state == PlaybackState.Ready;

        public int QueuedSpeechCount => _speechQueue.Count;

        private void Awake()
        {
            // Ensure we have an audio source
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
            }
            _audioSource.playOnAwake = false;
        }

        /// <summary>
        /// Assign a character to this player. If character is not loaded, it will be loaded automatically.
        /// If the same character is already assigned (and loading or loaded), this is a no-op.
        /// The player is <see cref="PlaybackState.Loading"/> until both the character data and
        /// its idle frames are in, then raises <see cref="OnReady"/>.
        /// </summary>
        public void AssignCharacter(Character character)
        {
            if (_character == character && character != null && _state != PlaybackState.Uninitialized)
            {
                // Same character, already loading or loaded.
                return;
            }
            
            // Stop current playback. A load of the previous character must not
            // finish into this one, so it is stopped here rather than in Stop():
            // Stop() itself deliberately leaves a load running (see there).
            Stop();
            // Stop() put the previous character's idle back on screen; the new
            // one is Loading, which shows nothing until its own frames are in.
            StopIdleAnimation();
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                _loadCoroutine = null;
            }
            _idleLoaded = false;
            _shownAvatarIndex = -1;
            _idleFrameIndex = 0;
            
            _character = character;
            
            if (character == null)
            {
                _state = PlaybackState.Uninitialized;
                return;
            }

            _state = PlaybackState.Loading;
            _loadCoroutine = StartCoroutine(LoadCharacterCoroutine(character));
        }

        /// <summary>
        /// Queue speech for the character. Speech is played in order; text is
        /// broken into lines/sentences for smoother playback.
        ///
        /// Guarantees:
        /// <list type="bullet">
        /// <item>May be called as soon as a character is assigned. While the player is
        /// <see cref="PlaybackState.Loading"/> the request is only queued and drains
        /// automatically when <see cref="OnReady"/> fires — no host-side wait is needed.</item>
        /// <item>While <see cref="PlaybackState.Speaking"/>, the request joins the current
        /// run: generation of the new line starts right away and the player stays Speaking
        /// through it (no intermediate <see cref="OnSpeechEnded"/> / <see cref="OnSpeechStarted"/>).</item>
        /// <item>While <see cref="PlaybackState.Paused"/> the request is queued and starts on
        /// <see cref="Resume"/>.</item>
        /// <item>Dropped, with a warning, only when there is no character or the assigned
        /// character failed to load (<see cref="PlaybackState.Uninitialized"/>), or when the
        /// text is empty.</item>
        /// </list>
        /// </summary>
        /// <param name="withAnimation">If false, plays audio only (useful for characters without avatars)</param>
        /// <param name="useCache">
        /// Forwarded to <see cref="Character.SpeakAsync"/>. False synthesises a
        /// new take of the same text and overwrites the cache; true (default)
        /// serves a matching wav when one exists.
        /// </param>
        public void QueueSpeech(string text, int expressionIndex = 0, bool withAnimation = true, bool useCache = true)
        {
            if (_character == null)
            {
                Logger.LogWarning("[CharacterPlayer] Cannot queue speech: no character assigned");
                return;
            }

            if (_state == PlaybackState.Uninitialized)
            {
                // A character is assigned but its load failed (a load in progress
                // is Loading, not Uninitialized). Nothing will ever drain the queue.
                Logger.LogWarning($"[CharacterPlayer] Cannot queue speech: character {_character.Name} did not load");
                return;
            }
            
            if (string.IsNullOrEmpty(text))
            {
                Logger.LogWarning("[CharacterPlayer] Cannot queue empty speech");
                return;
            }
            
            // Override animation if in audio-only mode or explicitly disabled
            if (audioOnly)
                withAnimation = false;
            
            // Break text into lines using TextUtils (same as SpeechPlaybackManager)
            var lines = TextUtils.BreakTextIntoLines(text);
            if (lines.Length == 0)
            {
                Logger.LogWarning("[CharacterPlayer] No lines after text processing");
                return;
            }
            
            var request = new SpeechRequest 
            { 
                TextLines = new List<string>(lines),
                ExpressionIndex = expressionIndex,
                WithAnimation = withAnimation,
                UseCache = useCache
            };
            
            _speechQueue.Enqueue(request);
            
            Logger.Log($"[CharacterPlayer] Queued speech: {lines.Length} lines from text: {text.Substring(0, Math.Min(50, text.Length))}... (Animation: {withAnimation}, State: {_state})");

            switch (_state)
            {
                case PlaybackState.Ready:
                case PlaybackState.Speaking:
                    // Ready: start. Speaking: make sure the processor is running —
                    // it exits when the queue empties, so a line queued during the
                    // last segment would otherwise sit until the player loop ends.
                    ProcessNextSpeech();
                    break;
                case PlaybackState.Loading:
                    Logger.Log("[CharacterPlayer] Character still loading — speech will start on Ready");
                    break;
                case PlaybackState.Paused:
                    Logger.Log("[CharacterPlayer] Player paused — speech will start on Resume");
                    break;
            }
        }

        /// <summary>
        /// Stop all playback and clear queues. Idle animation, the speech processor,
        /// the segment player and every in-flight frame collector are stopped; both
        /// queues are cleared. A character load that is still running is left to
        /// finish (it is not playback) and the player returns to
        /// <see cref="PlaybackState.Ready"/> once loaded, otherwise stays
        /// <see cref="PlaybackState.Loading"/>. With no character: Uninitialized.
        /// </summary>
        public void Stop()
        {
            // Retire every running coroutine first: anything still suspended sees
            // a stale epoch when it resumes and exits without touching state.
            _epoch++;

            StopIdleAnimation();

            if (_animationCoroutine != null)
            {
                StopCoroutine(_animationCoroutine);
                _animationCoroutine = null;
            }

            // The speech processor is *retired*, not killed. While it is suspended
            // inside Character.SpeakAsync the VoiceQueue lease is held by that
            // nested coroutine, and Unity documents nothing about running a
            // stopped iterator's finally blocks — least of all a nested one's.
            // Instead the processor checks the epoch after each SpeakAsync returns
            // and exits; the in-flight synthesis completes on its normal path,
            // which is the one place the lease is guaranteed to be released. The
            // result is discarded. MuseTalk generation runs on the API's own
            // controller and is never started or stopped from here, so its lease
            // is not at risk either.
            _speechCoroutine = null;

            // Copy first: if Unity does dispose a stopped iterator, the
            // collector's finally removes itself from this list.
            var collectors = _frameCollectors.ToArray();
            _frameCollectors.Clear();
            foreach (var collector in collectors)
            {
                if (collector.Handle != null)
                    StopCoroutine(collector.Handle);
            }
            
            if (_audioSource != null)
            {
                // Stop() rather than "if isPlaying": a paused source reports
                // isPlaying == false and would otherwise keep its clip.
                _audioSource.Stop();
                _audioSource.clip = null;
            }
            // Streamed lines: abandon the frames still being generated for them
            // (the playing one and any queued behind it), so the lip-sync engine
            // is free for whatever is queued next instead of finishing frames
            // nobody will display. The synthesis itself runs to completion on
            // its normal path, as for a batch line.
            _streamSource?.Cancel();
            foreach (var pending in _pendingAnimations)
                pending.Stream?.Cancel();
            ReleaseStreamClip();
            
            _speechQueue.Clear();
            _pendingAnimations.Clear();
            _playingItem = null;
            _speechRunStarted = false;

            // Reset processing flags so new speech can start fresh. Leaving
            // them set survives the stop: AnimationPlayerLoop is gated on
            // _isAnimationPlayerRunning, so the next request never starts its
            // player loop — audio plays and frames are generated, but none of
            // them are ever displayed, which reads as "the voice works and
            // lip-sync doesn't".
            _isSpeechProcessorRunning = false;
            _isAnimationPlayerRunning = false;
            _pausedWhileSpeaking = false;
            _idleWasRunningAtPause = false;

            if (_character == null)
            {
                _state = PlaybackState.Uninitialized;
            }
            else if (_idleLoaded)
            {
                _state = PlaybackState.Ready;
                if (!_destroyed && autoPlayIdle && !audioOnly && _idleFrames != null && _idleFrames.Count > 0)
                {
                    // Carry on from the cursor: the stop already cut the audio,
                    // no need to cut the face as well.
                    StartIdleAnimation(fromStart: false);
                }
            }
            else if (_loadCoroutine != null)
            {
                _state = PlaybackState.Loading;
            }
            else
            {
                _state = PlaybackState.Uninitialized;
            }
        }

        /// <summary>
        /// Called by <see cref="Character.ReplaceVoice"/>. Speech generated or
        /// queued in the old voice must not play as the new one, so if anything
        /// is speaking, queued or pending the pipeline is stopped (which puts
        /// idle back on screen, as any Stop does). A player that is simply
        /// Ready is left alone; its next <see cref="QueueSpeech"/> reads the
        /// character's current voice.
        /// </summary>
        internal void OnVoiceReplaced()
        {
            bool anythingInFlight = _state == PlaybackState.Speaking
                                    || _speechQueue.Count > 0
                                    || _pendingAnimations.Count > 0
                                    || _isSpeechProcessorRunning
                                    || _isAnimationPlayerRunning;
            if (!anythingInFlight)
                return;

            Logger.Log("[CharacterPlayer] Voice replaced while speech was in flight — dropping it");
            Stop();
        }

        /// <summary>
        /// Pause playback: idle animation stops, and if a line was playing its
        /// audio is paused and frame playback holds. No-op unless the player is
        /// Ready or Speaking. Speech queued while paused starts on <see cref="Resume"/>.
        /// </summary>
        public void Pause()
        {
            if (_state != PlaybackState.Ready && _state != PlaybackState.Speaking)
                return;

            _pausedWhileSpeaking = _state == PlaybackState.Speaking;
            _idleWasRunningAtPause = _idleCoroutine != null;

            if (_pausedWhileSpeaking && _audioSource != null)
            {
                _audioSource.Pause();
            }
            StopIdleAnimation();
            _state = PlaybackState.Paused;
        }

        /// <summary>
        /// Resume playback. Returns to <see cref="PlaybackState.Speaking"/> only if
        /// the player was speaking when paused; otherwise to
        /// <see cref="PlaybackState.Ready"/> with idle animation. Anything queued
        /// while paused then starts.
        /// </summary>
        public void Resume()
        {
            if (_state != PlaybackState.Paused)
                return;

            if (_pausedWhileSpeaking)
            {
                _state = PlaybackState.Speaking;
                if (_audioSource != null)
                    _audioSource.UnPause();
                // Paused between segments: the loop was idling while it waited
                // for the next one, so put the idle back.
                if (_idleWasRunningAtPause)
                    StartIdleAnimation(fromStart: false);
            }
            else
            {
                _state = PlaybackState.Ready;
                if (autoPlayIdle && !audioOnly && _idleFrames != null && _idleFrames.Count > 0)
                    StartIdleAnimation(fromStart: false);
            }

            _pausedWhileSpeaking = false;
            _idleWasRunningAtPause = false;

            if (_speechQueue.Count > 0)
                ProcessNextSpeech();
        }

        /// <summary>
        /// Clear all queued speech
        /// </summary>
        public void ClearQueue()
        {
            _speechQueue.Clear();
        }

        /// <summary>
        /// Loads character data if needed, then idle frames, then moves to Ready.
        /// Never writes the state while a speech is in flight (it cannot be — speech
        /// queued while Loading is only queued — but the guard is what the contract
        /// promises), and drains anything queued meanwhile.
        /// </summary>
        private IEnumerator LoadCharacterCoroutine(Character character)
        {
            if (!character.IsDataLoaded)
            {
                Logger.Log($"[CharacterPlayer] Loading character: {character.Name}");

                Exception loadError = null;
                yield return TaskYield.Guard(character.LoadData(),
                    ex => loadError = ex, "CharacterPlayer.LoadCharacter");

                if (loadError != null || !character.IsDataLoaded)
                {
                    loadError ??= new InvalidOperationException(
                        $"Character {character.Name} did not finish loading.");
                    Logger.LogError($"[CharacterPlayer] Failed to load character {character.Name}: {loadError.Message}");
                    _loadCoroutine = null;
                    _state = PlaybackState.Uninitialized;
                    _speechQueue.Clear();
                    OnError?.Invoke(loadError);
                    yield break;
                }

                Logger.Log($"[CharacterPlayer] Character loaded successfully: {character.Name}");
            }

            // Load idle frames with yielding
            yield return LoadIdleFramesCoroutine();
            
            // If no idle frames and not audio-only, use static character image as fallback
            if (_idleFrames == null || _idleFrames.Count == 0)
            {
                if (!audioOnly && _character?.Image != null)
                {
                    Logger.Log($"[CharacterPlayer] No idle frames loaded for {_character?.Name}. Using static character image.");
                    DisplayImage = _character.Image;
                    audioOnly = true; // Switch to audio-only mode for speech
                }
                else if (!audioOnly)
                {
                    Logger.LogWarning($"[CharacterPlayer] No idle frames or static image available for {_character?.Name}. Using audio-only mode.");
                    audioOnly = true;
                }
                else
                {
                    Logger.Log($"[CharacterPlayer] Audio-only mode for {_character?.Name}");
                }
            }

            _idleLoaded = true;
            _loadCoroutine = null;

            // Ready means "loaded and nothing in flight". If something is in
            // flight, leave the state alone: writing Ready/Idle here used to
            // land mid-speech and make PlayFramesSynchronized exit on entry.
            bool speechInFlight = _state == PlaybackState.Speaking
                                  || _isSpeechProcessorRunning
                                  || _isAnimationPlayerRunning;
            if (!speechInFlight)
            {
                _state = PlaybackState.Ready;
                if (autoPlayIdle && !audioOnly && _idleFrames != null && _idleFrames.Count > 0)
                {
                    StartIdleAnimation(fromStart: true);
                }
            }
            
            // Fire events AFTER idle frames are loaded and state is set
            OnReady?.Invoke();
            OnCharacterLoaded?.Invoke();

            // Drain whatever was queued while loading. Idle keeps playing until
            // the first segment is ready, as it does for any speech.
            if (_state == PlaybackState.Ready && _speechQueue.Count > 0)
            {
                Logger.Log($"[CharacterPlayer] Ready — starting {_speechQueue.Count} request(s) queued while loading");
                ProcessNextSpeech();
            }
        }

        private IEnumerator LoadIdleFramesCoroutine()
        {
            _idleFrames = new List<Texture>();
            
            if (_character == null)
            {
                Logger.LogWarning($"[CharacterPlayer] Cannot load idle frames: no character");
                yield break;
            }

            // Expression 0 of the character's avatar. Null for a voice-only
            // character, which is not a problem — it plays audio only.
            string expression0Folder = _character.IdleFramesFolder;
            if (string.IsNullOrEmpty(expression0Folder))
            {
                Logger.Log($"[CharacterPlayer] {_character.Name} has no animatable avatar; no idle frames");
                yield break;
            }
            
            if (!Directory.Exists(expression0Folder))
            {
                Logger.LogWarning($"[CharacterPlayer] Expression 0 folder not found: {expression0Folder}");
                yield break;
            }
            
            // Load all PNG frames from the expression folder
            var framePaths = Directory.GetFiles(expression0Folder, "*.png")
                .OrderBy(p => p)
                .ToArray();
            
            if (framePaths.Length == 0)
            {
                Logger.LogWarning($"[CharacterPlayer] No frames found in: {expression0Folder}");
                yield break;
            }
            
            // Load textures from disk with yielding between each
            foreach (var framePath in framePaths)
            {
                // Read file asynchronously. A single unreadable frame is skipped
                // (observed and logged), not fatal to the whole idle set.
                var readTask = File.ReadAllBytesAsync(framePath);
                yield return new WaitUntil(() => readTask.IsCompleted);
                
                if (readTask.IsCompletedSuccessfully)
                {
                    byte[] fileData = readTask.Result;
                    Texture2D texture = new(2, 2);
                    if (texture.LoadImage(fileData))
                    {
                        _idleFrames.Add(texture);
                    }
                }
                else if (readTask.IsFaulted)
                {
                    Logger.LogError($"[CharacterPlayer] Failed to read frame {framePath}: {readTask.Exception?.GetBaseException().Message}");
                }
                
                // Yield after each texture to avoid blocking
                yield return null;
            }
            
            if (_idleFrames.Count == 0)
            {
                Logger.LogWarning($"[CharacterPlayer] No frames loaded for character: {_character.Name}");
            }
            else
            {
                Logger.Log($"[CharacterPlayer] Loaded {_idleFrames.Count} idle frames from {expression0Folder}");
            }
        }

        /// <summary>
        /// Starts (or restarts) the idle loop. <paramref name="fromStart"/>
        /// rewinds to frame 0; otherwise the loop carries on from the cursor,
        /// which is where the last idle or lip-sync frame left it.
        /// </summary>
        private void StartIdleAnimation(bool fromStart)
        {
            if (_idleCoroutine != null)
            {
                StopCoroutine(_idleCoroutine);
            }

            if (fromStart)
            {
                _idleFrameIndex = 0;
            }
            else if (_idleFrames != null && _idleFrames.Count > 0)
            {
                _idleFrameIndex = Mod(_idleFrameIndex, _idleFrames.Count);
            }
            _idleNextFrameTime = Time.time;
            _idleCoroutine = StartCoroutine(PlayIdleAnimation());
            OnIdleStarted?.Invoke();
        }

        private void StopIdleAnimation()
        {
            if (_idleCoroutine != null)
            {
                StopCoroutine(_idleCoroutine);
                _idleCoroutine = null;
            }
        }

        /// <summary>
        /// Shows idle frames on a fixed schedule at the avatar's frame rate —
        /// frame <c>k</c> is due at <c>t0 + k / fps</c> — rather than sleeping
        /// a frame interval between frames, which rounds every wait up to the
        /// next rendered frame and ran 25 fps idle at 20 in a 60 Hz editor.
        /// Forward wrap: clips are rest-to-rest.
        /// </summary>
        private IEnumerator PlayIdleAnimation()
        {
            // Continue while idle coroutine is running (controlled by Start/Stop)
            // Don't check state - we explicitly start/stop this coroutine
            while (true)
            {
                if (_idleFrames == null || _idleFrames.Count == 0)
                {
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                float interval = 1f / IdleFps;
                if (Time.time >= _idleNextFrameTime)
                {
                    int count = _idleFrames.Count;
                    _idleFrameIndex = Mathf.Clamp(_idleFrameIndex, 0, count - 1);
                    // Index first: OnFrameUpdate subscribers read CurrentAvatarFrameIndex.
                    _shownAvatarIndex = _idleFrameIndex;
                    DisplayImage = _idleFrames[_idleFrameIndex];
                    _idleFrameIndex = (_idleFrameIndex + 1) % count;

                    // Next due time. After a hitch longer than a frame, resync
                    // to now rather than bursting through the backlog.
                    _idleNextFrameTime += interval;
                    if (Time.time - _idleNextFrameTime > interval)
                        _idleNextFrameTime = Time.time + interval;
                }

                yield return null;
            }
        }

        private static int Mod(int a, int m) => m <= 0 ? 0 : ((a % m) + m) % m;

        /// <summary>
        /// Moves the avatar cursor to lip-sync frame <paramref name="frameIndex"/>
        /// of a line that started on avatar frame <paramref name="start"/>, so
        /// idle resumes from the frame after the one speech ended on.
        /// </summary>
        private void MarkSpeechFrameShown(int start, int frameIndex)
        {
            if (start < 0 || _idleFrames == null || _idleFrames.Count == 0)
                return;
            int count = _idleFrames.Count;
            _shownAvatarIndex = Mod(start + frameIndex, count);
            _idleFrameIndex = Mod(start + frameIndex + 1, count);
        }

        /// <summary>
        /// Ensures both pipeline loops are running for whatever is queued. Idempotent:
        /// the running flags are set <i>before</i> <c>StartCoroutine</c>, so two calls in
        /// the same frame (or a call while a loop is mid-flight) cannot start a second
        /// loop. Enters Speaking (firing <see cref="OnSpeechStarted"/>) only on the
        /// transition; a call while already Speaking just tops up the pipeline.
        /// </summary>
        private void ProcessNextSpeech()
        {
            if (_speechQueue.Count == 0)
                return;

            if (_state != PlaybackState.Speaking)
            {
                _state = PlaybackState.Speaking;
                // DON'T stop idle animation yet - let it play while speech is being generated
                // AnimationPlayerLoop will stop it when first segment is ready to play.
                // OnSpeechStarted fires from MarkSpeechStarted when that first
                // frame (or audio-only clip) actually plays.
                _speechRunStarted = false;
                _speechQueuedAt = Time.realtimeSinceStartup;
                Logger.Log($"[CharacterPlayer] Speaking run queued at t={_speechQueuedAt:F3}s");
            }
            
            // Start both processor and player loops for pipelining. Flags first:
            // StartCoroutine runs the loop to its first yield synchronously, and
            // the player loop's exit condition reads the processor's flag.
            if (!_isSpeechProcessorRunning)
            {
                _isSpeechProcessorRunning = true;
                _speechCoroutine = StartCoroutine(SpeechProcessorLoop(_epoch));
            }
            
            if (!_isAnimationPlayerRunning)
            {
                _isAnimationPlayerRunning = true;
                _animationCoroutine = StartCoroutine(AnimationPlayerLoop(_epoch));
            }
        }

        /// <summary>
        /// Processes queued speech requests, generating audio/animation for each line.
        /// Audio generation is serialized, but animation collection happens in parallel.
        /// Runs in parallel with AnimationPlayerLoop for pipelining.
        /// </summary>
        /// <param name="epoch">
        /// The <see cref="_epoch"/> this loop belongs to. After <see cref="Stop"/> the
        /// loop is not stopped but retired: it lets the SpeakAsync it is inside finish
        /// (so the voice lease is released on its normal path), then exits without
        /// touching state.
        /// </param>
        private IEnumerator SpeechProcessorLoop(int epoch)
        {
            bool completed = false;
            try
            {
                while (epoch == _epoch && _speechQueue.Count > 0)
                {
                    var request = _speechQueue.Dequeue();
                    Logger.Log($"[CharacterPlayer] Processing speech request: {request.TextLines.Count} lines");
                    
                    foreach (var line in request.TextLines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        
                        Logger.Log($"[CharacterPlayer] Generating line: {line.Substring(0, Math.Min(50, line.Length))}...");
                        
                        // Create pending item for this line
                        var pendingItem = new PendingSpeechItem
                        {
                            Frames = new List<Texture>(),
                            AudioClip = null,
                            AudioReady = false,
                            AnimationReady = false,
                            FrameStream = null,
                            WithAnimation = request.WithAnimation
                        };
                        
                        _pendingAnimations.Enqueue(pendingItem);
                        
                        // For audio-only mode, generate only audio
                        if (!request.WithAnimation || audioOnly)
                        {
                            // Generate audio only using SpeakAsync with expressionIndex = -1
                            AudioClip audioClip = null;
                            bool hasError = false;
                            
                            IEnumerator audioCoroutine = _character.SpeakAsync(
                                line,
                                expressionIndex: -1, // -1 means audio-only
                                onAudioReady: (stream, clip) =>
                                {
                                    audioClip = clip;
                                    // stream will be null for audio-only
                                },
                                onAnimationComplete: null,
                                onError: (ex) =>
                                {
                                    hasError = true;
                                    ReportSpeechError(epoch, "Audio generation error", ex);
                                },
                                useCache: request.UseCache
                            );
                            
                            yield return audioCoroutine;

                            // Stopped while synthesising: the line is discarded.
                            if (epoch != _epoch)
                                yield break;
                            
                            if (audioClip != null && !hasError)
                            {
                                pendingItem.AudioClip = audioClip;
                                pendingItem.AudioReady = true;
                                pendingItem.AnimationReady = true; // No animation needed
                                Logger.Log($"[CharacterPlayer] Audio-only ready: {audioClip.length}s");
                            }
                            else
                            {
                                Logger.LogWarning($"[CharacterPlayer] Failed to generate audio for line");
                                pendingItem.AudioReady = true;
                                pendingItem.AnimationReady = true;
                            }
                            
                            continue;
                        }
                        
                        // Generate speech audio + animation
                        FrameStream frameStream = null;
                        AudioClip audioClip2 = null;
                        bool hasError2 = false;

                        // The generator asks for its start frame when it is
                        // about to begin, and we answer with where the idle
                        // loop (or the line before) will have got to by the
                        // time this line can play. See PredictStartFrame.
                        var itemForStart = pendingItem;
                        Func<int, int> startFrameProvider = expectedFrames => PredictStartFrame(itemForStart, expectedFrames);
                        
                        IEnumerator speechCoroutine = _character.SpeakAsync(
                            line,
                            request.ExpressionIndex,
                            onAudioReady: (stream, clip) =>
                            {
                                frameStream = stream;
                                audioClip2 = clip;
                                pendingItem.AudioClip = clip;
                                pendingItem.FrameStream = stream;
                                pendingItem.AudioReady = true;
                                Logger.Log($"[CharacterPlayer] Audio ready: {clip.length}s - can start next audio generation!");
                            },
                            onAnimationComplete: (stream) =>
                            {
                                Logger.Log($"[CharacterPlayer] Animation generation complete: {stream?.TotalExpectedFrames ?? 0} frames");
                            },
                            onError: (ex) =>
                            {
                                hasError2 = true;
                                ReportSpeechError(epoch, "Speech error", ex);
                            },
                            onStreamStarted: (speech) =>
                            {
                                // Streamed lip-sync: audio and frames are arriving
                                // now, seconds before onAudioReady. Start draining
                                // frames so the player loop can begin on the first.
                                if (epoch != _epoch)
                                {
                                    // Stopped before the first chunk: nobody will
                                    // play this line, so do not animate it.
                                    speech.Cancel();
                                    return;
                                }
                                pendingItem.Stream = speech;
                                pendingItem.FrameStream = speech.Frames;
                                frameStream = speech.Frames;
                                StartFrameCollector(pendingItem, speech.Frames);
                                Logger.Log($"[CharacterPlayer] Speech stream started at t={Time.realtimeSinceStartup:F3}s ({speech.SecondsAvailable:F2}s of audio)");
                            },
                            onSpeechChunk: null,
                            startFrameIndexProvider: startFrameProvider,
                            useCache: request.UseCache
                        );
                        
                        // Start the speech generation
                        yield return speechCoroutine;

                        // Stopped while synthesising: the line is discarded. Any
                        // animation the character started for it runs to completion
                        // on the API controller (that is where its lease lives).
                        if (epoch != _epoch)
                            yield break;
                        
                        if (hasError2 || audioClip2 == null || frameStream == null)
                        {
                            Logger.LogWarning($"[CharacterPlayer] Failed to generate speech for line");
                            pendingItem.AudioReady = true;
                            if (!pendingItem.CollectorStarted)
                                pendingItem.AnimationReady = true; // Mark as ready (but empty) so player can skip
                            continue;
                        }
                        
                        // Audio is ready! Start a separate coroutine to collect frames
                        // in parallel (already running for a streamed line).
                        StartFrameCollector(pendingItem, frameStream);
                        
                        // DON'T wait for animation - immediately continue to next line's audio generation
                        Logger.Log($"[CharacterPlayer] Audio done for line - starting next audio generation immediately!");
                    }
                }

                completed = true;
            }
            finally
            {
                // A retired loop (stale epoch) belongs to a previous Stop(); its
                // flags were reset there and may already describe a new loop.
                if (epoch == _epoch)
                {
                    _isSpeechProcessorRunning = false;
                    _speechCoroutine = null;

                    if (!completed)
                    {
                        // Died on an exception: release anything the player loop
                        // is waiting for, or it waits forever on an item that
                        // never becomes ready.
                        foreach (var item in _pendingAnimations)
                        {
                            item.AudioReady = true;
                            item.AnimationReady = true;
                        }
                    }
                }
            }

            Logger.Log("[CharacterPlayer] Speech processor loop ended");
        }

        /// <summary>
        /// Routes a SpeakAsync failure to <see cref="OnError"/> unless the loop that
        /// asked for it has since been retired by <see cref="Stop"/>.
        /// </summary>
        private void ReportSpeechError(int epoch, string what, Exception ex)
        {
            if (epoch != _epoch)
            {
                Logger.LogWarning($"[CharacterPlayer] {what} after Stop() (ignored): {ex.Message}");
                return;
            }
            Logger.LogError($"[CharacterPlayer] {what}: {ex.Message}");
            OnError?.Invoke(ex);
        }
        
        /// <summary>
        /// Answers the generator's "which avatar frame do I start on?" for
        /// <paramref name="item"/>, asked as generation is about to begin.
        ///
        /// If an earlier line is still pending or playing, this one follows it
        /// with no idle between, so it starts where that one ends. Otherwise
        /// the idle loop is running and will keep running until this line is
        /// ready: predict how long that is (batch: frames × learnt seconds per
        /// frame; streaming: a learnt flat lead) plus
        /// <see cref="ContinuityLeadSeconds"/>, and start that many frames
        /// ahead of the cursor. The player loop then waits for the idle to
        /// reach the frame (or cuts, past <see cref="MaxContinuityWaitSeconds"/>).
        /// Returns 0 when continuity does not apply.
        /// </summary>
        private int PredictStartFrame(PendingSpeechItem item, int expectedFrames)
        {
            item.GenerationStartedAt = Time.realtimeSinceStartup;
            item.GenerationFrames = expectedFrames;
            if (!ContinuityActive)
                return 0;

            int count = _idleFrames.Count;
            int start;

            // Chained: the line before this one in the queue (or on screen).
            PendingSpeechItem previous = null;
            foreach (var pending in _pendingAnimations)
            {
                if (ReferenceEquals(pending, item))
                    break;
                previous = pending;
            }
            if (previous == null && _playingItem != null && !ReferenceEquals(_playingItem, item))
                previous = _playingItem;

            int chainedEnd = previous?.ExpectedEnd(count) ?? -1;
            if (chainedEnd >= 0)
            {
                start = chainedEnd;
                Logger.LogVerbose($"[CharacterPlayer] Continuity: line follows the previous one, start frame {start}");
            }
            else
            {
                float lead = expectedFrames >= 0
                    ? expectedFrames * _batchSecondsPerFrame
                    : _streamLeadSeconds;
                lead += ContinuityLeadSeconds;
                int leadFrames = Mathf.RoundToInt(lead * IdleFps);
                start = Mod(_idleFrameIndex + leadFrames, count);
                Logger.LogVerbose($"[CharacterPlayer] Continuity: idle cursor {_idleFrameIndex}, predicted {lead:F2}s ({leadFrames} frames) to ready → start frame {start}");
            }

            item.PredictedStart = start;
            return start;
        }

        /// <summary>
        /// Feeds a line's measured generation time back into the estimator
        /// <see cref="PredictStartFrame"/> reads, once the line is ready to play.
        /// </summary>
        private void LearnGenerationTime(PendingSpeechItem item)
        {
            if (item.PredictedStart < 0 || item.GenerationStartedAt <= 0f)
                return;
            float elapsed = Time.realtimeSinceStartup - item.GenerationStartedAt;
            if (elapsed <= 0f)
                return;
            if (item.GenerationFrames > 0)
            {
                float perFrame = elapsed / item.GenerationFrames;
                _batchSecondsPerFrame = Mathf.Lerp(_batchSecondsPerFrame, perFrame, EstimatorGain);
                Logger.LogVerbose($"[CharacterPlayer] Continuity: batch line ready {elapsed:F2}s after start chosen ({perFrame * 1000f:F0} ms/frame; estimate now {_batchSecondsPerFrame * 1000f:F0} ms/frame)");
            }
            else
            {
                _streamLeadSeconds = Mathf.Lerp(_streamLeadSeconds, elapsed, EstimatorGain);
                Logger.LogVerbose($"[CharacterPlayer] Continuity: streamed line ready {elapsed:F2}s after start chosen (estimate now {_streamLeadSeconds:F2}s)");
            }
        }

        /// <summary>
        /// Brings the screen to the avatar frame a ready line starts on. With
        /// the idle running from the cursor, waits until its next frame is the
        /// line's first (bounded by <see cref="MaxContinuityWaitSeconds"/>),
        /// then stops it so the line's frame 0 lands on the idle's next tick.
        /// Beyond the bound it cuts and says by how much. No-op when
        /// continuity does not apply or the line's start is unknown.
        /// </summary>
        private IEnumerator AlignIdleToSpeechStart(PendingSpeechItem item)
        {
            int target = item.StartFrameIndex;
            if (!ContinuityActive || target < 0)
            {
                StopIdleAnimation();
                yield break;
            }

            int count = _idleFrames.Count;
            target = Mod(target, count);
            int cursor = Mod(_idleFrameIndex, count);
            int distance = Mod(target - cursor, count);
            int maxWaitFrames = Mathf.RoundToInt(MaxContinuityWaitSeconds * IdleFps);

            if (distance == 0)
            {
                Logger.LogVerbose($"[CharacterPlayer] Continuity: idle cursor {cursor} == speech start {target}; seamless");
            }
            else if (distance <= maxWaitFrames)
            {
                if (_idleCoroutine == null)
                    StartIdleAnimation(fromStart: false);
                float waitStart = Time.realtimeSinceStartup;
                float deadline = waitStart + MaxContinuityWaitSeconds + 1f;
                while (Mod(_idleFrameIndex, count) != target && Time.realtimeSinceStartup < deadline)
                {
                    if (_state != PlaybackState.Speaking && _state != PlaybackState.Paused)
                        break;
                    yield return null;
                }
                Logger.LogVerbose($"[CharacterPlayer] Continuity: idle run on {cursor} → {target} ({distance} frames, {Time.realtimeSinceStartup - waitStart:F2}s) ; seamless");
            }
            else
            {
                Logger.LogWarning($"[CharacterPlayer] Continuity: idle cursor {cursor} is {count - distance} frames past speech start {target} " +
                                  $"(waiting {distance} frames exceeds {MaxContinuityWaitSeconds:F1}s); cutting");
            }

            StopIdleAnimation();
            // The line's first frame is due when the idle's next frame was.
            while (Time.time < _idleNextFrameTime && _state == PlaybackState.Speaking)
                yield return null;
        }

        /// <summary>
        /// Starts draining <paramref name="frameStream"/> into the item's frame
        /// list, once per item. Tracked so <see cref="Stop"/> can find it.
        /// </summary>
        private void StartFrameCollector(PendingSpeechItem item, FrameStream frameStream)
        {
            if (item.CollectorStarted || frameStream == null)
                return;
            item.CollectorStarted = true;
            var collector = new FrameCollector();
            _frameCollectors.Add(collector);
            collector.Handle = StartCoroutine(CollectAnimationFrames(item, frameStream, collector));
        }

        /// <summary>
        /// Whether the player loop may start this item. Batch: audio and every
        /// frame are in. Streamed: the first frame exists and at least
        /// <see cref="PrerollSeconds"/> of audio is buffered (or the audio is
        /// already complete). Failed items are marked ready-and-empty so they
        /// are skipped rather than waited on.
        /// </summary>
        private bool IsItemReady(PendingSpeechItem item)
        {
            if (item.AudioReady && item.AnimationReady)
                return true;
            var stream = item.Stream;
            if (stream == null || item.Frames.Count == 0)
                return false;
            return stream.AudioFinished || stream.SecondsAvailable >= EffectivePrerollSeconds(stream.SampleRate);
        }

        /// <summary>
        /// <see cref="PrerollSeconds"/>, raised to cover what the mixer reads
        /// ahead on Play: a streaming clip's reader is asked for two blocks up
        /// front, so a preroll smaller than that would starve the moment
        /// playback began. The block size is learnt from the first line.
        /// </summary>
        private float EffectivePrerollSeconds(int hz)
        {
            float readAhead = (2f * Mathf.Max(_streamMaxBlock, 4096)) / hz;
            return Mathf.Max(PrerollSeconds, readAhead + 0.1f);
        }

        /// <summary>
        /// First audible/visible moment of a Speaking run: raises
        /// <see cref="OnSpeechStarted"/> once and logs the latency since the run
        /// was queued.
        /// </summary>
        private void MarkSpeechStarted(string how)
        {
            if (_speechRunStarted)
                return;
            _speechRunStarted = true;
            float now = Time.realtimeSinceStartup;
            Logger.Log($"[CharacterPlayer] Speech started ({how}) at t={now:F3}s, {now - _speechQueuedAt:F3}s after queue");
            OnSpeechStarted?.Invoke();
        }

        /// <summary>
        /// Collects animation frames in parallel with audio generation for next segment.
        /// </summary>
        private IEnumerator CollectAnimationFrames(PendingSpeechItem item, FrameStream frameStream, FrameCollector self)
        {
            try
            {
                Logger.Log($"[CharacterPlayer] Starting animation frame collection in parallel...");
                
                // Collect all frames. A failed producer marks the stream finished
                // too, so this exits either way; Error tells the two apart.
                while (frameStream.HasMoreFrames)
                {
                    var awaiter = frameStream.WaitForNext();
                    yield return awaiter;
                    
                    if (awaiter.Texture != null)
                    {
                        item.Frames.Add(awaiter.Texture);
                    }
                }

                if (frameStream.Error != null)
                {
                    Logger.LogWarning($"[CharacterPlayer] Animation failed after {item.Frames.Count} frame(s); segment plays with what arrived: {frameStream.Error.Message}");
                }
                else
                {
                    Logger.Log($"[CharacterPlayer] Animation frames collected: {item.Frames.Count} frames");
                }
            }
            finally
            {
                // Mark animation as ready (on every exit: the player loop must
                // never wait on an item nobody is filling).
                item.AnimationReady = true;
                _frameCollectors.Remove(self);
            }
        }

        /// <summary>
        /// Plays generated animation frames synchronized with audio.
        /// Runs in parallel with SpeechProcessorLoop for pipelining.
        /// Returns to idle animation only if waiting for next segment.
        /// Holds while Paused. On completion: restarts the pipeline if more speech
        /// was queued meanwhile, otherwise returns the player to Ready.
        /// </summary>
        private IEnumerator AnimationPlayerLoop(int epoch)
        {
            bool completed = false;
            try
            {
                bool isFirstSegment = true;
                
                while (_isSpeechProcessorRunning || _pendingAnimations.Count > 0)
                {
                    // Check if we need to wait for next segment
                    bool needsToWait = _pendingAnimations.Count == 0 || !IsItemReady(_pendingAnimations.Peek());
                    
                    if (needsToWait && !isFirstSegment && _state != PlaybackState.Paused)
                    {
                        // Next segment not ready - return to idle while waiting,
                        // carrying on from the frame the last line ended on.
                        Logger.Log("[CharacterPlayer] Next segment not ready - returning to idle while waiting");
                        StartIdleAnimation(fromStart: false);
                    }
                    
                    // Wait for next segment to be ready (idle animates during this wait)
                    while (_pendingAnimations.Count == 0 || !IsItemReady(_pendingAnimations.Peek()))
                    {
                        yield return new WaitForSeconds(0.05f);
                        
                        // Exit if processor finished and no more pending
                        if (!_isSpeechProcessorRunning && _pendingAnimations.Count == 0)
                        {
                            break;
                        }
                    }
                    
                    if (_pendingAnimations.Count == 0)
                        break;
                    
                    var item = _pendingAnimations.Dequeue();
                    _playingItem = item;
                    LearnGenerationTime(item);

                    // Paused while waiting: hold before touching the display.
                    while (_state == PlaybackState.Paused)
                        yield return null;
                    
                    // A streamed line plays from its stream even if synthesis
                    // failed part-way: what arrived is played, then the segment ends.
                    bool streamed = item.Stream != null;

                    // Skip empty items
                    if (item.AudioClip == null && !streamed)
                    {
                        Logger.LogWarning("[CharacterPlayer] Skipping empty speech item");
                        continue;
                    }
                    
                    // For audio-only playback
                    if (!streamed && (!item.WithAnimation || item.Frames.Count == 0))
                    {
                        Logger.Log($"[CharacterPlayer] Playing audio-only: {item.AudioClip.length}s");
                        
                        // Just play the audio
                        _audioSource.clip = item.AudioClip;
                        _audioSource.Play();
                        MarkSpeechStarted("audio only");
                        
                        // Wait for audio to finish (a paused source is not
                        // "playing", so hold on the state as well)
                        while (_state == PlaybackState.Paused || _audioSource.isPlaying)
                        {
                            yield return new WaitForSeconds(0.1f);
                        }
                        
                        // After playing, we're no longer in first segment
                        isFirstSegment = false;
                        continue;
                    }
                    
                    // Leave idle for speech.
                    if (ContinuityActive)
                    {
                        // The line was rendered from the frame the idle was
                        // predicted to reach; run the idle on to it (or cut,
                        // past the bound), then hand over on the beat.
                        yield return AlignIdleToSpeechStart(item);
                    }
                    else if (needsToWait || isFirstSegment)
                    {
                        // Continuity off: the line starts on the clip's first
                        // frame, so show the last idle frame and cut.
                        Logger.Log("[CharacterPlayer] Segment ready - transitioning from idle to speech");
                        StopIdleAnimation();
                        
                        if (_idleFrames != null && _idleFrames.Count > 0)
                        {
                            Texture lastIdleFrame = _idleFrames[^1];
                            _shownAvatarIndex = _idleFrames.Count - 1;
                            DisplayImage = lastIdleFrame;
                            
                            // Brief pause to show transition
                            yield return new WaitForSeconds(0.04f);
                        }
                    }
                    else
                    {
                        // Next segment was already ready - play immediately (no idle transition)
                        Logger.Log("[CharacterPlayer] Next segment ready - playing immediately");
                    }
                    
                    if (streamed)
                    {
                        Logger.Log($"[CharacterPlayer] Playing streamed segment: {item.Frames.Count} frames and {item.Stream.SecondsAvailable:F2}s of audio so far");
                        yield return PlayStreamingSegment(item);
                    }
                    else
                    {
                        Logger.Log($"[CharacterPlayer] Playing segment: {item.Frames.Count} frames, {item.AudioClip.length}s");
                        
                        // Play this segment with its audio
                        yield return PlayFramesSynchronized(item.Frames, item.AudioClip, item.StartFrameIndex);
                    }
                    
                    // After playing, we're no longer in first segment
                    isFirstSegment = false;
                    _playingItem = null;
                }

                completed = true;
            }
            finally
            {
                _playingItem = null;
                // Exception path only. Normal completion continues below (and a
                // stopped coroutine has a stale epoch — Stop() reset the flags).
                if (!completed && epoch == _epoch)
                {
                    _isAnimationPlayerRunning = false;
                    _animationCoroutine = null;
                    if (_state == PlaybackState.Speaking)
                        _state = _idleLoaded ? PlaybackState.Ready : PlaybackState.Loading;
                }
            }

            Logger.Log("[CharacterPlayer] Animation player loop ended");

            // Finish while paused would put idle on screen under a Pause().
            while (_state == PlaybackState.Paused)
                yield return null;

            if (epoch != _epoch)
                yield break;

            _isAnimationPlayerRunning = false;
            _animationCoroutine = null;

            // Lines queued during the last segment: keep going, still Speaking.
            // (QueueSpeech restarts the processor itself; this is the backstop
            // for lines queued while paused, or in the same frame the loop ended.)
            if (_speechQueue.Count > 0)
            {
                Logger.Log($"[CharacterPlayer] {_speechQueue.Count} request(s) still queued - continuing without returning to idle");
                ProcessNextSpeech();
                yield break;
            }

            // Speech complete
            _state = PlaybackState.Ready;
            OnSpeechEnded?.Invoke();
            
            // Return to idle from the frame after the one speech ended on.
            if (autoPlayIdle && !audioOnly && _idleFrames != null && _idleFrames.Count > 0)
            {
                StartIdleAnimation(fromStart: false);
            }
        }

        private IEnumerator PlayFramesSynchronized(List<Texture> frames, AudioClip audioClip, int startFrameIndex = -1)
        {
            if (frames.Count == 0 || audioClip == null)
            {
                yield break;
            }
            
            // Calculate frame interval based on audio duration and frame count
            float frameInterval = audioClip.length / frames.Count;
            
            // Start audio playback
            _audioSource.clip = audioClip;
            _audioSource.Play();
            
            // Play frames on a schedule off the audio clock — frame i is due at
            // i * interval — so the display cannot drift from the audio the way
            // a sleep per frame (rounded up to the next rendered frame) did.
            // Holds while Paused (audio is paused by Pause()); exits only if
            // the player left Speaking some other way.
            for (int i = 0; i < frames.Count; i++)
            {
                while (_state == PlaybackState.Paused)
                    yield return null;
                if (_state != PlaybackState.Speaking)
                    yield break;

                MarkSpeechFrameShown(startFrameIndex, i);
                DisplayImage = frames[i];
                if (i == 0)
                    MarkSpeechStarted("batch");

                float due = (i + 1) * frameInterval;
                while (_state == PlaybackState.Paused
                       || (_state == PlaybackState.Speaking && _audioSource.isPlaying && _audioSource.time < due))
                    yield return null;
            }
            
            // Wait for audio to finish
            while (_state == PlaybackState.Paused
                   || (_audioSource.isPlaying && _state == PlaybackState.Speaking))
            {
                yield return null;
            }
        }

        /// <summary>
        /// Plays a line whose audio and frames are still arriving.
        ///
        /// <para><b>Audio.</b> One streaming <see cref="AudioClip"/>
        /// (<c>AudioClip.Create(..., stream: true, reader)</c>) whose reader
        /// copies from the <see cref="SpeechStream"/> on the audio thread. Every
        /// chunk lands in one contiguous buffer, so there is no seam to schedule
        /// or crossfade and <see cref="AudioSource.timeSamples"/> is one
        /// monotonic clock for the whole line. That clock, not wall time,
        /// decides which frame is up: frame <c>i</c> shows at sample
        /// <c>i * rate / 25</c>, so drift cannot accumulate and a pause holds
        /// both together.</para>
        ///
        /// <para><b>Starvation.</b> If synthesis falls behind playback the
        /// source is paused until <see cref="PrerollSeconds"/> is buffered
        /// again, rather than letting the reader hand the mixer silence. If
        /// generation falls behind (frames slower than real time) the last frame
        /// is held — never skipped forward — and one warning names the deficit.
        /// The segment ends when the audio has been fully played.</para>
        /// </summary>
        private IEnumerator PlayStreamingSegment(PendingSpeechItem item)
        {
            var stream = item.Stream;
            int hz = stream.SampleRate;

            ReleaseStreamClip();
            _streamReadPos = 0;
            _streamUnderruns = 0;
            _streamSource = stream;
            _streamClip = AudioClip.Create("LiveTalkStreamedSpeech", hz * MaxStreamedSeconds, 1, hz, true,
                OnStreamPcmRead, OnStreamPcmSetPosition);
            _audioSource.clip = _streamClip;
            _audioSource.Play();

            int shown = -1;
            int displayed = 0;
            int startFrame = item.StartFrameIndex;
            int heldTicks = 0;
            int maxDeficit = 0;
            bool holdWarned = false;
            int starvations = 0;
            int lastPos = 0;

            try
            {
                while (true)
                {
                    while (_state == PlaybackState.Paused)
                        yield return null;
                    if (_state != PlaybackState.Speaking)
                        yield break;

                    int pos = _audioSource.timeSamples;
                    lastPos = pos;

                    // Audio about to outrun synthesis: hold the source rather than
                    // let the reader fill the mixer with zeros. The mixer reads
                    // ahead of the play position in blocks, so the guard watches
                    // the reader's position and keeps a block (at least 0.1 s) of
                    // real samples ahead of it, and resumes once the preroll is
                    // buffered again. The low-water mark stays below the resume
                    // level so the two cannot chase each other.
                    int readAhead = stream.SamplesAvailable - _streamReadPos;
                    int resumeAt = Mathf.Max(hz / 10, Mathf.RoundToInt(EffectivePrerollSeconds(hz) * hz));
                    int lowWater = Mathf.Min(Mathf.Max(_streamMaxBlock, hz / 10), resumeAt / 2);
                    if (!stream.AudioFinished && readAhead < lowWater)
                    {
                        starvations++;
                        if (starvations == 1)
                            Logger.LogWarning($"[CharacterPlayer] Streamed audio caught up with synthesis at {pos / (float)hz:F2}s; holding until {resumeAt / (float)hz:F2}s is buffered");
                        _audioSource.Pause();
                        do
                        {
                            yield return null;
                            if (_state != PlaybackState.Speaking && _state != PlaybackState.Paused)
                                yield break;
                        }
                        while (!stream.AudioFinished && stream.SamplesAvailable - _streamReadPos < resumeAt);
                        if (_state == PlaybackState.Speaking)
                            _audioSource.UnPause();
                        continue;
                    }

                    // Frame due at this audio position.
                    int target = (int)((long)pos * 25 / hz);
                    if (target > shown)
                    {
                        int available = item.Frames.Count;
                        if (target < available)
                        {
                            shown = target;
                            MarkSpeechFrameShown(startFrame, shown);
                            DisplayImage = item.Frames[shown];
                            displayed++;
                            MarkSpeechStarted("streamed");
                        }
                        else if (available > 0)
                        {
                            // Generation is behind the audio: show the newest frame
                            // we do have and hold it.
                            if (available - 1 > shown)
                            {
                                shown = available - 1;
                                MarkSpeechFrameShown(startFrame, shown);
                                DisplayImage = item.Frames[shown];
                                displayed++;
                                MarkSpeechStarted("streamed");
                            }
                            if (!item.AnimationReady)
                            {
                                heldTicks++;
                                int deficit = target - (available - 1);
                                maxDeficit = Math.Max(maxDeficit, deficit);
                                if (!holdWarned)
                                {
                                    holdWarned = true;
                                    Logger.LogWarning($"[CharacterPlayer] Audio ahead of frames: frame {target} due, {available} generated (deficit {deficit}); holding the last frame");
                                }
                            }
                        }
                    }

                    if (stream.AudioFinished && pos >= stream.SamplesAvailable)
                        break;
                    if (stream.AudioFinished && !_audioSource.isPlaying)
                        break;

                    yield return null;
                }

                Logger.Log($"[CharacterPlayer] Streamed segment done: {displayed} frames displayed (last index {shown}) of {item.Frames.Count} generated, " +
                           $"{lastPos / (float)hz:F2}s audio, held on {heldTicks} tick(s) (max deficit {maxDeficit} frames), " +
                           $"{starvations} audio hold(s), {_streamUnderruns} reader underrun(s), reader block {_streamMaxBlock} samples");
            }
            finally
            {
                if (_audioSource != null && _audioSource.clip == _streamClip)
                {
                    _audioSource.Stop();
                    _audioSource.clip = null;
                }
                ReleaseStreamClip();
            }
        }

        /// <summary>Audio thread: fill the mixer's buffer from the stream at the reader position.</summary>
        private void OnStreamPcmRead(float[] data)
        {
            var source = _streamSource;
            if (source == null)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }
            if (data.Length > _streamMaxBlock)
                _streamMaxBlock = data.Length;
            int copied = source.ReadSamples(_streamReadPos, data, 0, data.Length);
            _streamReadPos += data.Length;
            if (copied < data.Length && !source.AudioFinished)
                System.Threading.Interlocked.Increment(ref _streamUnderruns);
        }

        private void OnStreamPcmSetPosition(int position)
        {
            _streamReadPos = position;
        }

        private void ReleaseStreamClip()
        {
            _streamSource = null;
            if (_streamClip != null)
            {
                var clip = _streamClip;
                _streamClip = null;
                if (Application.isPlaying)
                    Destroy(clip);
                else
                    DestroyImmediate(clip);
            }
        }

        private void OnDestroy()
        {
            // Stop() restarts idle when the character is loaded; not on a
            // GameObject that is going away.
            _destroyed = true;
            Stop();
        }
    }
}
