using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LiveTalk.API
{
    using Core;
    using Utils;

    /// <summary>One utterance of a rendered performance, with its wav on disk.</summary>
    public sealed class RenderedUtterance
    {
        public Character Character { get; internal set; }
        public string CharacterId { get; internal set; }
        public string CharacterName { get; internal set; }
        public float Start { get; internal set; }
        public float Duration { get; internal set; }
        public float End => Start + Duration;
        public string WavPath { get; internal set; }
        public string Caption { get; internal set; }
        public bool LipSync { get; internal set; }
    }

    /// <summary>
    /// A <see cref="Performance"/> after <see cref="LiveTalkAPI.RenderPerformanceAsync"/>:
    /// a folder under the LiveTalk cache holding the manifest, plus (per
    /// animated character) one frame path per tick. Frames are PNG files —
    /// the avatar's own driving frames, pose-cache renders, and the
    /// lip-synced composites — referenced, not copied. Play with
    /// <see cref="PerformancePlayer"/>, or read the frame lists to encode a
    /// video.
    /// </summary>
    public sealed class RenderedPerformance
    {
        public string Folder { get; private set; }
        public string Fingerprint { get; private set; }
        public float Fps { get; private set; }
        public int TickCount { get; private set; }
        public float Duration { get; private set; }

        private readonly Dictionary<string, string[]> _frames = new();
        private readonly Dictionary<string, Character> _characters = new();
        private readonly List<RenderedUtterance> _utterances = new();
        private readonly List<CaptionEvent> _captions = new();

        public IReadOnlyList<RenderedUtterance> Utterances => _utterances;
        public IReadOnlyList<CaptionEvent> Captions => _captions;

        /// <summary>Character ids that have a frame track.</summary>
        public IReadOnlyCollection<string> AnimatedCharacterIds => _frames.Keys;

        /// <summary>The character for an id, when the source performance is known.</summary>
        public Character CharacterFor(string id) => _characters.TryGetValue(id, out var c) ? c : null;

        /// <summary>Frame PNG path per tick for a character, or null when it has no avatar.</summary>
        public string[] FramesFor(string characterId) => _frames.TryGetValue(characterId, out var f) ? f : null;

        public string[] FramesFor(Character character) => character == null ? null : FramesFor(character.Id);

        public string ManifestPath => Path.Combine(Folder, PerformanceRenderer.ManifestFileName);

        internal static RenderedPerformance From(PerformanceManifest m, string folder, Performance source)
        {
            var r = new RenderedPerformance
            {
                Folder = folder,
                Fingerprint = m.fingerprint,
                Fps = m.fps,
                TickCount = m.tickCount,
                Duration = m.duration,
            };
            if (source != null)
                foreach (var c in source.Characters) r._characters[c.Id] = c;

            foreach (var c in m.characters)
                r._frames[c.characterId] = c.frames;
            foreach (var u in m.utterances)
            {
                r._utterances.Add(new RenderedUtterance
                {
                    Character = r.CharacterFor(u.characterId),
                    CharacterId = u.characterId,
                    CharacterName = u.characterName,
                    Start = u.start,
                    Duration = u.duration,
                    WavPath = u.wav,
                    Caption = u.caption,
                    LipSync = u.lipSync,
                });
            }
            foreach (var c in m.captions)
                r._captions.Add(new CaptionEvent(r.CharacterFor(c.characterId), c.text, c.start, c.end));
            return r;
        }

        /// <summary>Reads a manifest; null when unreadable or when a referenced frame is missing.</summary>
        internal static RenderedPerformance Load(string manifestPath, Performance source)
        {
            try
            {
                var m = JsonConvert.DeserializeObject<PerformanceManifest>(File.ReadAllText(manifestPath));
                if (m == null || m.tickCount <= 0) return null;
                foreach (var c in m.characters)
                {
                    if (c.frames == null || c.frames.Length != m.tickCount) return null;
                    // Spot-check: first, last and a middle frame still exist
                    // (the pose cache or the avatar may have been cleared).
                    if (!File.Exists(c.frames[0]) || !File.Exists(c.frames[^1]) || !File.Exists(c.frames[c.frames.Length / 2]))
                        return null;
                }
                foreach (var u in m.utterances)
                    if (!File.Exists(u.wav)) return null;
                return From(m, Path.GetDirectoryName(manifestPath), source);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Performance] Could not load {manifestPath}: {ex.Message}");
                return null;
            }
        }
    }
}
