using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>Immutable, explicit scene knowledge. Unknown scenes never acquire a HUD identity or camera-lock permission.</summary>
internal sealed record CombatSceneCapabilities(CombatHudSlot? FixedHealthSlot = null,
    bool PreferFixedTarget = false, bool AllowTargetLock = false)
{
    internal static readonly CombatSceneCapabilities Unknown = new();

    // 2026-10-13 run ed6a5311ddbb4fbc98e4fa3772627c66, originals 2753/2755 (1920x1080).
    // Only the fixed HUD slot is established. These frames prove neither target-lock behavior
    // nor that component/add targets may be ignored, so both input permissions stay false.
    internal static readonly CombatSceneCapabilities AlgorithmOfSemiIntransientMatrix = new(
        new CombatHudSlot(716d / 1920, 80d / 1080, 1204d / 1920, 96d / 1080));
}

internal readonly record struct CombatHudSlot(double Left, double Top, double Right, double Bottom)
{
    internal bool Contains(EnemySeekVisual visual, int width, int height) => width > 0 && height > 0 &&
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) && double.IsFinite(Bottom) &&
        Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 && Left < Right && Top < Bottom &&
        visual.X >= Left * width && visual.Y >= Top * height &&
        visual.X + Math.Max(visual.Width, visual.HealthBarTrackWidth) <= Right * width &&
        visual.Y + visual.Height <= Bottom * height;
}
