using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LiveTalk.API
{
    using Utils;

    /// <summary>
    /// Plays a <see cref="RenderedPerformance"/> on one clock: frames for
    /// every animated character, wavs for every utterance, captions. Frames
    /// are streamed from disk a little ahead of the play head (the whole
    /// scene is never resident). Create with
    /// <see cref="LiveTalkAPI.CreatePerformancePlayer"/>; the host owns
    /// <c>Destroy</c>.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class PerformancePlayer : MonoBehaviour
    {
        public enum State { Empty, Loading, Ready, Playing, Paused, Ended }

        /// <summary>Frames decoded ahead of the play head.</summary>
        public int Lookahead = 40;

        public State PlaybackState { get; private set; } = State.Empty;
        public RenderedPerformance Performance { get; private set; }

        /// <summary>Seconds on the performance clock.</summary>
        public float Time { get; private set; }

        public int CurrentTick => Performance == null ? 0 : Mathf.Clamp(Mathf.FloorToInt(Time * Performance.Fps), 0, Performance.TickCount - 1);
        public bool IsPlaying => PlaybackState == State.Playing;

        /// <summary>A new frame for a character (the texture is the player's; do not destroy it).</summary>
        public event Action<string, Texture> OnFrame;

        /// <summary>An utterance began (character id, caption text).</summary>
        public event Action<RenderedUtterance> OnUtteranceStarted;

        /// <summary>A caption should show / clear.</summary>
        public event Action<CaptionEvent> OnCaption;
        public event Action<string> OnCaptionCleared;

        public event Action OnReady;
        public event Action OnEnded;
        public event Action<Exception> OnError;

        // frames
        sealed class Track
        {
            public string CharacterId;
            public string[] Files;
            public readonly Dictionary<int, Texture2D> Loaded = new();
            public readonly Stack<Texture2D> Pool = new();
            public int LastShown = -1;
            public int LoadCursor;
            public bool Loading;
        }

        readonly List<Track> _tracks = new();
        readonly Dictionary<string, AudioSource> _sources = new();
        readonly Dictionary<RenderedUtterance, AudioClip> _clips = new();
        readonly HashSet<RenderedUtterance> _fired = new();
        readonly HashSet<CaptionEvent> _shown = new();
        readonly Dictionary<string, CaptionEvent?> _activeCaption = new();
        Coroutine _loader;

        // ───────────────────────── load ─────────────────────────

        public void Load(RenderedPerformance performance)
        {
            if (performance == null) throw new ArgumentNullException(nameof(performance));
            Stop();
            Clear();
            Performance = performance;
            PlaybackState = State.Loading;
            StartCoroutine(LoadRoutine());
        }

        IEnumerator LoadRoutine()
        {
            foreach (string id in Performance.AnimatedCharacterIds)
                _tracks.Add(new Track { CharacterId = id, Files = Performance.FramesFor(id) });

            // Every utterance's wav (small; loaded once).
            foreach (var u in Performance.Utterances)
            {
                var task = AudioFileIO.LoadClipAsync(u.WavPath);
                yield return new WaitUntil(() => task.IsCompleted);
                if (task.IsFaulted || task.Result == null)
                {
                    var ex = task.Exception?.GetBaseException() ?? new IOException("no clip");
                    PlaybackState = State.Empty;
                    OnError?.Invoke(new IOException($"Could not load {u.WavPath}: {ex.Message}", ex));
                    yield break;
                }
                _clips[u] = task.Result;
                if (!_sources.ContainsKey(u.CharacterId))
                {
                    var go = new GameObject("Voice_" + u.CharacterName);
                    go.transform.SetParent(transform, false);
                    var src = go.AddComponent<AudioSource>();
                    src.playOnAwake = false;
                    src.spatialBlend = 0f;
                    src.volume = 1f;
                    _sources[u.CharacterId] = src;
                }
            }

            Time = 0f;
            // Prime the first frames so Play shows something immediately.
            yield return PrimeAsync(0);
            PlaybackState = State.Ready;
            OnReady?.Invoke();
        }

        IEnumerator PrimeAsync(int fromTick)
        {
            foreach (var t in _tracks)
            {
                ReleaseAll(t);
                t.LoadCursor = fromTick;
                int upTo = Mathf.Min(t.Files.Length, fromTick + Mathf.Max(2, Lookahead / 4));
                while (t.LoadCursor < upTo)
                    yield return LoadOne(t);
                ShowTick(t, fromTick);
            }
        }

        // ───────────────────────── transport ─────────────────────────

        public void Play()
        {
            if (PlaybackState is not (State.Ready or State.Paused or State.Ended)) return;
            if (PlaybackState == State.Ended) Seek(0f);
            PlaybackState = State.Playing;
            foreach (var s in _sources.Values) if (s.clip != null && s.time > 0f) s.UnPause();
            _loader ??= StartCoroutine(LoaderLoop());
        }

        public void Pause()
        {
            if (PlaybackState != State.Playing) return;
            PlaybackState = State.Paused;
            foreach (var s in _sources.Values) if (s.isPlaying) s.Pause();
        }

        public void Resume() => Play();

        public void Stop()
        {
            if (PlaybackState is State.Empty or State.Loading) return;
            PlaybackState = State.Ready;
            foreach (var s in _sources.Values) s.Stop();
            Time = 0f;
            _fired.Clear();
            _shown.Clear();
            foreach (var id in new List<string>(_activeCaption.Keys)) ClearCaption(id);
        }

        /// <summary>Jumps the clock. Utterances that start before the new time do not replay.</summary>
        public void Seek(float seconds)
        {
            if (Performance == null) return;
            Time = Mathf.Clamp(seconds, 0f, Performance.Duration);
            foreach (var s in _sources.Values) s.Stop();
            _fired.Clear();
            _shown.Clear();
            foreach (var u in Performance.Utterances)
                if (u.Start < Time) _fired.Add(u);
            foreach (var c in Performance.Captions)
                if (c.End <= Time) _shown.Add(c);
            foreach (var id in new List<string>(_activeCaption.Keys)) ClearCaption(id);
            StartCoroutine(PrimeAsync(CurrentTick));
            if (PlaybackState == State.Ended) PlaybackState = State.Ready;
        }

        void Update()
        {
            if (PlaybackState != State.Playing || Performance == null) return;

            Time += UnityEngine.Time.unscaledDeltaTime;
            int tick = CurrentTick;

            foreach (var t in _tracks)
                if (t.LastShown != tick) ShowTick(t, tick);

            foreach (var u in Performance.Utterances)
            {
                if (_fired.Contains(u) || Time < u.Start) continue;
                _fired.Add(u);
                if (_clips.TryGetValue(u, out var clip) && _sources.TryGetValue(u.CharacterId, out var src))
                {
                    src.Stop();
                    src.clip = clip;
                    // Late start (a hitch): skip into the clip so it stays on the clock.
                    src.time = Mathf.Clamp(Time - u.Start, 0f, Mathf.Max(0f, clip.length - 0.01f));
                    src.Play();
                }
                OnUtteranceStarted?.Invoke(u);
            }

            foreach (var c in Performance.Captions)
            {
                string id = c.Character?.Id ?? "";
                if (!_shown.Contains(c) && Time >= c.Start && Time < c.End)
                {
                    _shown.Add(c);
                    _activeCaption[id] = c;
                    OnCaption?.Invoke(c);
                }
                else if (_activeCaption.TryGetValue(id, out var active) && active.HasValue
                         && active.Value.Equals(c) && Time >= c.End)
                {
                    ClearCaption(id);
                }
            }

            if (Time >= Performance.Duration)
            {
                PlaybackState = State.Ended;
                foreach (var id in new List<string>(_activeCaption.Keys)) ClearCaption(id);
                OnEnded?.Invoke();
            }
        }

        void ClearCaption(string id)
        {
            if (_activeCaption.TryGetValue(id, out var c) && c.HasValue)
            {
                _activeCaption[id] = null;
                OnCaptionCleared?.Invoke(id);
            }
        }

        // ───────────────────────── frames ─────────────────────────

        void ShowTick(Track t, int tick)
        {
            if (t.Loaded.TryGetValue(tick, out var tex))
            {
                t.LastShown = tick;
                OnFrame?.Invoke(t.CharacterId, tex);
            }
            // Not loaded yet: keep the last frame (never skip backwards).
        }

        IEnumerator LoaderLoop()
        {
            while (Performance != null && PlaybackState is not (State.Empty or State.Loading))
            {
                int tick = CurrentTick;
                bool didWork = false;
                foreach (var t in _tracks)
                {
                    // Release frames behind the head.
                    if (t.Loaded.Count > 0)
                    {
                        List<int> drop = null;
                        foreach (int k in t.Loaded.Keys)
                            if (k < tick - 1) (drop ??= new List<int>()).Add(k);
                        if (drop != null)
                            foreach (int k in drop) { t.Pool.Push(t.Loaded[k]); t.Loaded.Remove(k); }
                    }
                    if (t.LoadCursor < tick) t.LoadCursor = tick;
                    if (t.LoadCursor < t.Files.Length && t.LoadCursor < tick + Lookahead)
                    {
                        yield return LoadOne(t);
                        didWork = true;
                    }
                }
                if (!didWork) yield return null;
            }
            _loader = null;
        }

        IEnumerator LoadOne(Track t)
        {
            int k = t.LoadCursor++;
            if (t.Loaded.ContainsKey(k)) yield break;
            string path = t.Files[k];
            var read = File.ReadAllBytesAsync(path);
            while (!read.IsCompleted) yield return null;
            if (read.IsFaulted)
            {
                Logger.LogWarning($"[PerformancePlayer] Frame {k} unreadable: {path}");
                yield break;
            }
            var tex = t.Pool.Count > 0 ? t.Pool.Pop() : new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (tex.LoadImage(read.Result))
                t.Loaded[k] = tex;
            else
                t.Pool.Push(tex);
        }

        void ReleaseAll(Track t)
        {
            foreach (var tex in t.Loaded.Values) t.Pool.Push(tex);
            t.Loaded.Clear();
        }

        void Clear()
        {
            foreach (var t in _tracks)
            {
                foreach (var tex in t.Loaded.Values) Destroy(tex);
                foreach (var tex in t.Pool) Destroy(tex);
            }
            _tracks.Clear();
            foreach (var s in _sources.Values) if (s != null) Destroy(s.gameObject);
            _sources.Clear();
            foreach (var c in _clips.Values) if (c != null) Destroy(c);
            _clips.Clear();
            _fired.Clear();
            _shown.Clear();
            _activeCaption.Clear();
            if (_loader != null) { StopCoroutine(_loader); _loader = null; }
        }

        void OnDestroy() => Clear();
    }
}
