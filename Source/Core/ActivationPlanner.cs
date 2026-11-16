namespace MoreUseResources.Core
{
    public enum ActivationReason
    {
        Active = 0,
        ForcedByUser = 1,
        MasterOff = 2,
        ResourceOff = 3,
        PhaseOff = 4,
        PausedByUser = 5,
        Unfocused = 6,
        WorldViewOff = 7,
        Quitting = 8,
        FrameGuard = 9,
        Unavailable = 10,
        Faulted = 11,
    }

    public readonly struct ResourceActivation
    {
        public readonly bool Active;
        /// <summary>0..1</summary>
        public readonly float Intensity;
        public readonly ActivationReason Reason;

        public ResourceActivation(bool active, float intensity, ActivationReason reason)
        {
            Active = active;
            Intensity = intensity;
            Reason = reason;
        }

        public static ResourceActivation Off(ActivationReason reason) => new ResourceActivation(false, 0f, reason);
    }

    public readonly struct ActivationPlan
    {
        public readonly PhaseSnapshot Phase;
        public readonly ResourceActivation Cpu;
        public readonly ResourceActivation Ram;
        public readonly ResourceActivation Gpu;

        public ActivationPlan(PhaseSnapshot phase, ResourceActivation cpu, ResourceActivation ram, ResourceActivation gpu)
        {
            Phase = phase;
            Cpu = cpu;
            Ram = ram;
            Gpu = gpu;
        }

        public ResourceActivation For(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Cpu: return Cpu;
                case ResourceKind.Ram: return Ram;
                default: return Gpu;
            }
        }
    }

    /// <summary>Pure decision: which resource may work right now, and how hard.</summary>
    public static class ActivationPlanner
    {
        public static ActivationPlan Plan(ResourceSettings settings, PhaseSnapshot phase, ManualMode manual)
        {
            return new ActivationPlan(
                phase,
                PlanOne(settings, phase, manual, ResourceKind.Cpu),
                PlanOne(settings, phase, manual, ResourceKind.Ram),
                PlanOne(settings, phase, manual, ResourceKind.Gpu));
        }

        public static ResourceActivation PlanOne(ResourceSettings s, PhaseSnapshot phase, ManualMode manual, ResourceKind kind)
        {
            // Safety states win over everything, including a manual "run now".
            if (phase.Phase == GamePhase.Quitting)
            {
                return ResourceActivation.Off(ActivationReason.Quitting);
            }
            if (!s.Enabled)
            {
                return ResourceActivation.Off(ActivationReason.MasterOff);
            }
            if (!s.ResourceEnabled(kind))
            {
                return ResourceActivation.Off(ActivationReason.ResourceOff);
            }
            if (manual == ManualMode.Paused)
            {
                return ResourceActivation.Off(ActivationReason.PausedByUser);
            }

            int index = (int)phase.Phase;
            // Main-thread GPU work can never run before the game finished loading its data,
            // and "run now" must not override the user's choice to keep saving undisturbed.
            if (kind == ResourceKind.Gpu && phase.Phase == GamePhase.Startup)
            {
                return ResourceActivation.Off(ActivationReason.PhaseOff);
            }
            if (phase.Has(PhaseModifiers.Unfocused) && !s.AllowWhenUnfocused)
            {
                return ResourceActivation.Off(ActivationReason.Unfocused);
            }
            if (phase.Has(PhaseModifiers.WorldView) && !s.AllowInWorldView)
            {
                return ResourceActivation.Off(ActivationReason.WorldViewOff);
            }

            if (manual == ManualMode.ForceRun && phase.Phase != GamePhase.Saving)
            {
                return new ResourceActivation(true, 1f, ActivationReason.ForcedByUser);
            }

            if (!s.PhasesFor(kind)[index])
            {
                return ResourceActivation.Off(ActivationReason.PhaseOff);
            }
            float intensity = s.PhaseIntensity[index] / 100f;
            if (s.IdleBoost && phase.Has(PhaseModifiers.Idle) && phase.Phase != GamePhase.Saving)
            {
                intensity = 1f;
            }
            if (intensity < 0.05f) intensity = 0.05f;
            if (intensity > 1f) intensity = 1f;
            return new ResourceActivation(true, intensity, ActivationReason.Active);
        }
    }
}
