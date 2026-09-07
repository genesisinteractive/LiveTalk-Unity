using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveTalk.API
{
    /// <summary>How an expression cue behaves once its clip has started.</summary>
    public enum ExpressionMode
    {
        /// <summary>Play the driving clip from rest to rest, then return to idle.</summary>
        PlayThrough = 0,

        /// <summary>
        /// Play the clip to <see cref="ExpressionCue.Peak01"/>, then hold that
        /// pose for <see cref="ExpressionCue.HoldSeconds"/> with the idle clip's
        /// own sway added at <see cref="ExpressionCue.Micro01"/> (eye keypoints
        /// at 1.0 so blinks stay real), then blend back to idle.
        /// </summary>
        HoldAtPeak = 1,
    }

    /// <summary>Handle to a cue on a <see cref="Performance"/>; use with <see cref="Anchor"/>.</summary>
    public readonly struct CueId : IEquatable<CueId>
    {
        internal readonly int Value;
        internal CueId(int value) { Value = value; }
        public bool IsValid => Value > 0;
        public bool Equals(CueId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is CueId o && Equals(o);
        public override int GetHashCode() => Value;
        public override string ToString() => IsValid ? $"cue#{Value}" : "cue#none";
    }

    /// <summary>
    /// Where a cue sits on the clock: an absolute second, or an offset from
    /// another cue's start or end. Relative anchors are what let a reaction
    /// be authored against a line whose length is only known once its audio
    /// exists.
    /// </summary>
    public readonly struct Anchor
    {
        internal enum Kind { Absolute, StartOf, EndOf, AfterPrevious }

        internal readonly Kind Type;
        internal readonly CueId Ref;
        public readonly float Offset;

        private Anchor(Kind type, CueId reference, float offset)
        {
            Type = type;
            Ref = reference;
            Offset = offset;
        }

        /// <summary>At <paramref name="seconds"/> on the performance clock.</summary>
        public static Anchor At(float seconds) => new(Kind.Absolute, default, seconds);

        /// <summary><paramref name="offset"/> seconds after the start of <paramref name="cue"/> (negative = before).</summary>
        public static Anchor Start(CueId cue, float offset = 0f) => new(Kind.StartOf, cue, offset);

        /// <summary><paramref name="offset"/> seconds after the end of <paramref name="cue"/> (negative = overlap).</summary>
        public static Anchor End(CueId cue, float offset = 0f) => new(Kind.EndOf, cue, offset);

        /// <summary>
        /// The default for an utterance: after the previous utterance added to
        /// the performance (any character) ends, plus <paramref name="offset"/>
        /// (negative = interrupt). For the first utterance it is the start.
        /// </summary>
        public static Anchor AfterPrevious(float offset = 0f) => new(Kind.AfterPrevious, default, offset);

        public override string ToString() => Type switch
        {
            Kind.Absolute => $"@{Offset:0.00}s",
            Kind.StartOf => $"start({Ref}){Offset:+0.00;-0.00}",
            Kind.EndOf => $"end({Ref}){Offset:+0.00;-0.00}",
            _ => $"after-previous{Offset:+0.00;-0.00}",
        };
    }

    /// <summary>An expression the face performs, independent of any speech.</summary>
    public sealed class ExpressionCue
    {
        public CueId Id { get; internal set; }
        public Character Character { get; }
        public int Expression { get; }
        public Anchor At { get; }
        public ExpressionMode Mode { get; set; } = ExpressionMode.PlayThrough;

        /// <summary>Where in the clip the peak is, 0–1. Used by <see cref="ExpressionMode.HoldAtPeak"/>.</summary>
        public float Peak01 { get; set; } = 0.6f;

        /// <summary>How long to stay at the peak.</summary>
        public float HoldSeconds { get; set; } = 1.5f;

        /// <summary>0–1: how much of the idle clip's sway rides the held pose. Eyes are always 1.</summary>
        public float Micro01 { get; set; } = 0.15f;

        /// <summary>Seconds to blend from whatever the face was doing into this clip. 0 cuts.</summary>
        public float BlendIn { get; set; } = 0.4f;

        /// <summary>Seconds to blend from this clip's last pose back into idle (or the next cue).</summary>
        public float BlendOut { get; set; } = 0.4f;

        internal ExpressionCue(Character character, int expression, Anchor at)
        {
            Character = character;
            Expression = expression;
            At = at;
        }

        public override string ToString() => $"{Id} expr {Expression} {Mode} {At}";
    }

    /// <summary>One spoken line by one character.</summary>
    public sealed class Utterance
    {
        public CueId Id { get; internal set; }
        public Character Character { get; }
        public string Text { get; }
        public Anchor At { get; }

        /// <summary>
        /// True: MuseTalk lip-sync over the expression track (needs an avatar).
        /// False: audio only — a vocalisation (<c>mmm</c>, <c>woah</c>) that
        /// rides the face as it is, or a character with no avatar.
        /// </summary>
        public bool LipSync { get; set; } = true;

        /// <summary>Caption text; null shows <see cref="Text"/>.</summary>
        public string Caption { get; set; }

        internal Utterance(Character character, string text, Anchor at)
        {
            Character = character;
            Text = text;
            At = at;
        }

        public override string ToString() =>
            $"{Id} {Character?.Name}: \"{(Text.Length > 32 ? Text[..30] + "…" : Text)}\" {At}";
    }

    /// <summary>
    /// A timed scene for one or more characters: an expression track and a
    /// speech track on one 25 fps clock. Built by a host, rendered once by
    /// <see cref="LiveTalkAPI.RenderPerformanceAsync"/>, played by
    /// <see cref="PerformancePlayer"/>. Nothing here touches models; it is data.
    /// </summary>
    public sealed class Performance
    {
        public const float Fps = 25f;

        /// <summary>Default gap between consecutive utterances (<see cref="Anchor.AfterPrevious"/>).</summary>
        public float DefaultGap { get; set; } = 0.3f;

        /// <summary>Idle time appended after the last cue so the scene does not end on a cut.</summary>
        public float Tail { get; set; } = 0.6f;

        internal readonly List<ExpressionCue> Expressions = new();
        internal readonly List<Utterance> Utterances = new();
        private int _nextId = 1;

        public IReadOnlyList<ExpressionCue> ExpressionCues => Expressions;
        public IReadOnlyList<Utterance> UtteranceCues => Utterances;

        /// <summary>Every distinct character on either track, in first-seen order.</summary>
        public IReadOnlyList<Character> Characters
        {
            get
            {
                var list = new List<Character>();
                foreach (var u in Utterances) if (u.Character != null && !list.Contains(u.Character)) list.Add(u.Character);
                foreach (var e in Expressions) if (e.Character != null && !list.Contains(e.Character)) list.Add(e.Character);
                return list;
            }
        }

        public ExpressionCue AddExpression(Character character, int expression, Anchor at)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (expression < 0) throw new ArgumentOutOfRangeException(nameof(expression));
            var cue = new ExpressionCue(character, expression, at) { Id = new CueId(_nextId++) };
            Expressions.Add(cue);
            return cue;
        }

        public Utterance AddUtterance(Character character, string text, Anchor at)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Utterance text is empty.", nameof(text));
            var u = new Utterance(character, text, at) { Id = new CueId(_nextId++) };
            Utterances.Add(u);
            return u;
        }

        /// <summary>Convenience: the next line, <see cref="DefaultGap"/> after the previous one.</summary>
        public Utterance AddUtterance(Character character, string text) =>
            AddUtterance(character, text, Anchor.AfterPrevious(DefaultGap));

        internal Utterance PreviousUtterance(Utterance of)
        {
            int i = Utterances.IndexOf(of);
            return i > 0 ? Utterances[i - 1] : null;
        }

        internal Utterance PreviousUtterance(ExpressionCue before)
        {
            // An expression cue's "previous utterance" is the last one added
            // before it (by id order).
            Utterance best = null;
            foreach (var u in Utterances)
                if (u.Id.Value < before.Id.Value && (best == null || u.Id.Value > best.Id.Value)) best = u;
            return best;
        }

        internal object Find(CueId id)
        {
            foreach (var u in Utterances) if (u.Id.Equals(id)) return u;
            foreach (var e in Expressions) if (e.Id.Equals(id)) return e;
            return null;
        }

        /// <summary>
        /// Content fingerprint of the authored cues (not of the audio or
        /// avatars). Two performances with the same fingerprint, characters
        /// and voices render the same thing.
        /// </summary>
        public string Fingerprint()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("perf_v1;gap=").Append(DefaultGap.ToString("R")).Append(";tail=").Append(Tail.ToString("R")).Append(';');
            foreach (var u in Utterances)
                sb.Append("U|").Append(u.Id.Value).Append('|').Append(u.Character?.Id).Append('|')
                  .Append(u.Character?.Voice?.Id).Append('|').Append(u.Character?.Avatar?.Id).Append('|')
                  .Append(u.Text).Append('|').Append(u.At).Append('|').Append(u.LipSync ? 1 : 0).Append(';');
            foreach (var e in Expressions)
                sb.Append("E|").Append(e.Id.Value).Append('|').Append(e.Character?.Id).Append('|')
                  .Append(e.Character?.Avatar?.Id).Append('|').Append(e.Expression).Append('|')
                  .Append(e.At).Append('|').Append((int)e.Mode).Append('|')
                  .Append(e.Peak01.ToString("R")).Append('|').Append(e.HoldSeconds.ToString("R")).Append('|')
                  .Append(e.Micro01.ToString("R")).Append('|').Append(e.BlendIn.ToString("R")).Append('|')
                  .Append(e.BlendOut.ToString("R")).Append(';');
            return Utils.HashUtils.GenerateTextHash(sb.ToString());
        }
    }

    /// <summary>A caption to show, from <see cref="Start"/> to <see cref="End"/> on the performance clock.</summary>
    public readonly struct CaptionEvent
    {
        public readonly Character Character;
        public readonly string Text;
        public readonly float Start;
        public readonly float End;

        public CaptionEvent(Character character, string text, float start, float end)
        {
            Character = character;
            Text = text;
            Start = start;
            End = end;
        }
    }
}
