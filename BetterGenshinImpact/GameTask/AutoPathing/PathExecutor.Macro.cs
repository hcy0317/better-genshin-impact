using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using System;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public partial class PathExecutor
{
    private bool IsKnownPathTransformation(ImageRegion frame) => _pathingMacro != null &&
        frame.FrameStamp.IsFresh(_moveIo.Clock, TimeSpan.FromMilliseconds(150)) &&
        SaurianUiReader.IsKnownTransformation(frame);

    private void SendPathForward(KeyType type)
    {
        if (_pathingMacro?.TryNavigationForward(type == KeyType.KeyDown, ct) == true) return;
        _moveIo.Send(GIActions.MoveForward, type);
    }

    private bool ReleaseMacroBeforeRevive(ImageRegion frame)
    {
        if (!Bv.IsCombatHud(frame)) _pathingMacro?.Release();
        return true;
    }
}
