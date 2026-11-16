using System;

namespace MoreUseResources.Core
{
    /// <summary>Exclusive primary phase of the game. Order matters only for array indexing.</summary>
    public enum GamePhase
    {
        Startup = 0,
        MainMenu = 1,
        Loading = 2,
        PlayingNormal = 3,
        PlayingFast = 4,
        Paused = 5,
        Saving = 6,
        /// <summary>Not configurable: nothing may start while quitting.</summary>
        Quitting = 7,
    }

    [Flags]
    public enum PhaseModifiers
    {
        None = 0,
        WorldView = 1,
        Idle = 2,
        Unfocused = 4,
    }

    public enum ProgramStage
    {
        Entry = 0,
        MapInitializing = 1,
        Playing = 2,
    }

    /// <summary>Raw facts sampled from the game. Kept free of game types so it can be unit tested.</summary>
    public struct RawGameSignals
    {
        public bool Quitting;
        public bool Saving;
        public bool PlayDataLoaded;
        public bool LongEventActive;
        public ProgramStage Stage;
        public bool Paused;
        public bool FastSpeed;
        public bool WorldView;
        public bool Focused;
        public float SecondsSinceInput;
    }

    public readonly struct PhaseSnapshot : IEquatable<PhaseSnapshot>
    {
        public readonly GamePhase Phase;
        public readonly PhaseModifiers Modifiers;

        public PhaseSnapshot(GamePhase phase, PhaseModifiers modifiers)
        {
            Phase = phase;
            Modifiers = modifiers;
        }

        public bool Has(PhaseModifiers m) => (Modifiers & m) != 0;
        public bool Equals(PhaseSnapshot other) => Phase == other.Phase && Modifiers == other.Modifiers;
        public override bool Equals(object? obj) => obj is PhaseSnapshot o && Equals(o);
        public override int GetHashCode() => ((int)Phase * 397) ^ (int)Modifiers;
        public override string ToString() => Modifiers == PhaseModifiers.None ? Phase.ToString() : Phase + " [" + Modifiers + "]";
    }

    public static class PhaseResolver
    {
        public const int ConfigurablePhaseCount = 7;

        /// <summary>
        /// Resolves one exclusive phase. Priority (safest first):
        /// Quitting, Saving, Startup, Loading, Paused, PlayingFast, PlayingNormal, MainMenu.
        /// </summary>
        public static PhaseSnapshot Resolve(in RawGameSignals s, float idleThresholdSeconds)
        {
            GamePhase phase;
            if (s.Quitting)
            {
                phase = GamePhase.Quitting;
            }
            else if (s.Saving)
            {
                phase = GamePhase.Saving;
            }
            else if (!s.PlayDataLoaded)
            {
                phase = GamePhase.Startup;
            }
            else if (s.LongEventActive || s.Stage == ProgramStage.MapInitializing)
            {
                phase = GamePhase.Loading;
            }
            else if (s.Stage == ProgramStage.Playing)
            {
                if (s.Paused)
                {
                    phase = GamePhase.Paused;
                }
                else
                {
                    phase = s.FastSpeed ? GamePhase.PlayingFast : GamePhase.PlayingNormal;
                }
            }
            else
            {
                phase = GamePhase.MainMenu;
            }

            PhaseModifiers mods = PhaseModifiers.None;
            if (s.WorldView && s.Stage == ProgramStage.Playing)
            {
                mods |= PhaseModifiers.WorldView;
            }
            if (!s.Focused)
            {
                mods |= PhaseModifiers.Unfocused;
            }
            if (idleThresholdSeconds > 0f && s.SecondsSinceInput >= idleThresholdSeconds)
            {
                mods |= PhaseModifiers.Idle;
            }
            return new PhaseSnapshot(phase, mods);
        }
    }
}
