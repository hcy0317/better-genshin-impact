using System.Collections.Generic;

namespace BetterGenshinImpact.Core.Mask;

// Shared by the WPF renderer and offline producer/snapshot replay. No WPF instance.
public static class MaskWindowDrawingVisibility
{
    public static IEnumerable<MaskWindowDrawingEntry> GetEntries(MaskWindowDrawingSnapshot snapshot, bool showRecognition)
    {
        foreach (var entry in snapshot.Entries)
            if (showRecognition || entry.Group.Kind != MaskWindowDrawingKind.Recognition)
                yield return entry;
    }
}
