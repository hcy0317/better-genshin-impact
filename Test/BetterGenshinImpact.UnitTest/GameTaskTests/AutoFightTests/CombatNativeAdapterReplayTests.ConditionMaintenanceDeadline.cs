using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public async Task MaintenanceReturnCannotPoisonTheNextActorWithATailBudgetConditionSelection()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var program = LoadProgram(LoadPartyScript("00-岩.txt"));
        using var io = new PhysicalReplay(clock, false, 10, program)
        {
            Actors = RockParty, CompleteSelectionReceipts = true, ExternalClockAdvance = true
        };
        io.SetFrontActor("香菱");
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        const double originalDeadline = 19.1454963;
        var maintenanceDue = false;
        var condition = new CombatFlowAction(new("香菱", "q(recharge,from=segment:供能)"),
            context, () => true, originalDeadline, shouldYield: () => maintenanceDue);
        var conditionId = condition.CommandId;

        async Task Reach(double at)
        {
            Assert.True(context.Now <= at + .000001, $"Replay precondition overshot {at:F7}: {context.Now:F7}");
            var milliseconds = (int)Math.Floor(Math.Max(0, at - context.Now) * 1000);
            await io.DelayAsync(milliseconds, parent.Token);
            if (context.Now < at) clock.Advance(TimeSpan.FromSeconds(at - context.Now));
        }

        // The original recharge demand is observed, not credited as a successful burst.
        await Reach(15.5287348);
        var preparation = CombatObservationPreparation.AwaitingObservation;
        for (var i = 0; i < 20 && preparation != CombatObservationPreparation.Ready; i++)
        {
            adapter.BeginStep();
            if (adapter.HasObservationRequest) await adapter.AdvanceObservationAsync(parent.Token);
            preparation = await adapter.PrepareObservationStepAsync(condition, "q-ready", parent.Token);
            if (preparation != CombatObservationPreparation.Ready) await adapter.YieldAsync(parent.Token);
        }
        Assert.Equal(CombatObservationPreparation.Ready, preparation);
        Assert.False(Assert.IsType<bool>(adapter.Observe("q-ready", [], "香菱")));
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Selections);

        // Exercise the same public cancellation/maintenance adapter operations as the
        // scheduler. The 15-second shield budget cannot renew the old condition deadline.
        await Reach(16.2373897);
        maintenanceDue = true;
        adapter.CancelObservation(condition);
        var maintenance = new CombatFlowAction(new("钟离",
            "e(hold,wait,timing=离线护盾,record=护盾,maintain=护盾,required)"),
            context, () => true, 31.2134857);
        var maintenanceResult = CombatFlowResult.AwaitingObservation;
        CombatSkillAttempt? pendingMaintenance = null;
        CombatFlowAction? confirmedMaintenance = null;
        var maintenanceTrace = new Queue<string>();
        for (var i = 0; i < 150 && context.Now < 18.836 && maintenanceResult != CombatFlowResult.Succeeded; i++)
        {
            var enteredAt = context.Now;
            // CombatFlowExecution creates a fresh confirmation-only action after Pending.
            // Reusing the original action (which already has InputAt) cannot claim that
            // confirmation and instead enters the old cast's explicit cooldown wait.
            var stepAction = pendingMaintenance == null ? maintenance : new CombatFlowAction(
                maintenance.Command, context, () => true,
                Math.Min(maintenance.AbsoluteDeadline, pendingMaintenance.Deadline),
                confirmationAttempt: pendingMaintenance);
            adapter.BeginStep();
            if (adapter.HasObservationRequest) await adapter.AdvanceObservationAsync(parent.Token);
            var advancedAt = context.Now;
            var ready = await adapter.PrepareObservationStepAsync(stepAction, "e-ready", parent.Token);
            var preparedAt = context.Now;
            if (ready == CombatObservationPreparation.Ready)
                maintenanceResult = await adapter.ExecuteAsync(stepAction, parent.Token);
            if (maintenanceResult == CombatFlowResult.Pending && stepAction.PendingAttempt is { } submitted)
                pendingMaintenance = submitted;
            if (maintenanceResult == CombatFlowResult.Succeeded) confirmedMaintenance = stepAction;
            var executedAt = context.Now;
            if (maintenanceResult != CombatFlowResult.Succeeded) await adapter.YieldAsync(parent.Token);
            maintenanceTrace.Enqueue($"{i}:start={enteredAt:F7} advance={advancedAt:F7} prepare={preparedAt:F7}/{ready} execute={executedAt:F7}/{maintenanceResult} yield={context.Now:F7} pending={adapter.HasObservationRequest} confirmationOnly={stepAction.IsConfirmationOnly} inputAt={stepAction.InputAt?.ToString("F7") ?? "none"} effective={stepAction.EffectiveInputAt?.ToString("F7") ?? "none"} reason={stepAction.DiagnosticReason}");
            if (maintenanceTrace.Count > 16) maintenanceTrace.Dequeue();
        }
        Assert.True(maintenanceResult == CombatFlowResult.Succeeded,
            $"Maintenance precondition: actual={maintenanceResult}, now={context.Now:F7}, selections={string.Join(",", io.Selections)}, inputs={string.Join(",", io.Inputs)}. {string.Join(" | ", maintenanceTrace)}");
        var shieldInput = Assert.Single(io.Inputs);
        Assert.Equal(("钟离", Method.Skill), (shieldInput.Actor, shieldInput.Skill));
        Assert.NotNull(maintenance.EffectiveInputAt);
        Assert.NotNull(confirmedMaintenance);
        Assert.True(confirmedMaintenance!.IsConfirmationOnly);
        Assert.Equal(maintenance.CommandId, confirmedMaintenance.CommandId);
        Assert.Null(confirmedMaintenance.InputAt);
        Assert.Equal(shieldInput.At, confirmedMaintenance.EffectiveInputAt);
        Assert.False(io.HoldingInput);
        await Reach(18.836);
        var selectionCountAfterMaintenance = io.Selections.Count;
        double? tailRemaining = null;
        io.BeforeSelectionInput = () => tailRemaining ??= condition.RemainingBudget;

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await Reach(18.865);
            maintenanceDue = false;
            adapter.BeginStep();
            await adapter.PrepareObservationStepAsync(condition, "q-ready", parent.Token);
            if (adapter.HasObservationRequest)
            {
                // A new physical pulse here has the recorded .186-second remainder;
                // the game's one-second switch response is still an external outcome.
                await Reach(originalDeadline - .186 - .010);
                adapter.BeginStep();
                await adapter.AdvanceObservationAsync(parent.Token);
            }
            for (var i = 0; i < 20 && adapter.HasObservationRequest; i++)
            {
                await adapter.YieldAsync(parent.Token);
                adapter.BeginStep();
                await adapter.AdvanceObservationAsync(parent.Token);
            }
            if (context.Now < originalDeadline + .04) await Reach(originalDeadline + .04);
            var next = new CombatFlowAction(new("娜维娅", "q(if=q-ready(娜维娅))"),
                context, () => true, context.Now + 8);
            adapter.BeginStep();
            await adapter.PrepareObservationStepAsync(next, "q-ready", parent.Token);
        });

        Assert.False(parent.IsCancellationRequested);
        Assert.Equal(conditionId, condition.CommandId);
        Assert.Equal(originalDeadline, condition.AbsoluteDeadline);
        Assert.Null(condition.EffectiveInputAt);
        Assert.Single(io.Inputs); // Only the independently confirmed maintenance E.
        Assert.True(failure == null,
            $"Maintenance returned to an unfulfilled condition selection; tailRemaining={tailRemaining?.ToString("F7") ?? "no-input"}. {failure}");
        Assert.DoesNotContain(4, io.Selections.Skip(selectionCountAfterMaintenance));
        Assert.False(io.HoldingInput);
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationPreparationKeepsReadyForBudgetedSwitchAndOnFieldFrames(bool alreadyOnField)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var program = LoadProgram("香菱 q(required)");
        using var io = new PhysicalReplay(clock, false, 10, program)
        { Actors = RockParty, CompleteSelectionReceipts = true };
        io.SetFrontActor(alreadyOnField ? "香菱" : "钟离");
        io.ScheduleEnergy(0, "香菱", true);
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var deadline = alreadyOnField ? .25 : 4;
        var action = new CombatFlowAction(new("香菱", "q(required)"), context, () => true, deadline);
        var commandId = action.CommandId;
        var preparation = CombatObservationPreparation.AwaitingObservation;

        for (var i = 0; i < 100 && context.Now < deadline && preparation != CombatObservationPreparation.Ready; i++)
        {
            adapter.BeginStep();
            if (adapter.HasObservationRequest) await adapter.AdvanceObservationAsync(parent.Token);
            preparation = await adapter.PrepareObservationStepAsync(action, "q-ready", parent.Token);
            if (preparation != CombatObservationPreparation.Ready) await adapter.YieldAsync(parent.Token);
        }

        Assert.Equal(CombatObservationPreparation.Ready, preparation);
        Assert.True(context.Now < deadline);
        Assert.Equal(deadline, action.AbsoluteDeadline);
        Assert.Equal(commandId, action.CommandId);
        var targetFrames = io.ActiveObservations.Where(item => item.Index == 4)
            .Select(item => item.Source).Distinct().ToArray();
        Assert.True(targetFrames.Length >= 2, "Ready requires two distinct observed target frames.");
        Assert.True(targetFrames[^1].IsAfter(targetFrames[^2]));
        if (alreadyOnField) Assert.Empty(io.Selections);
        else
        {
            Assert.NotEmpty(io.Selections);
            Assert.All(io.Selections, index => Assert.Equal(4, index));
            Assert.True(targetFrames[^2].CapturedTimestamp > io.LastSelectionTimestamp);
        }
        Assert.True(Assert.IsType<bool>(adapter.Observe("q-ready", [], "香菱")));
        // Selection/preparation Ready grants observation, not a required-Q completion.
        Assert.Null(action.InputAt);
        Assert.Null(action.EffectiveInputAt);
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Primitives);
        Assert.False(parent.IsCancellationRequested);
        Assert.False(io.HoldingInput);
        Assert.Null(System.Windows.Application.Current);
    }
}
