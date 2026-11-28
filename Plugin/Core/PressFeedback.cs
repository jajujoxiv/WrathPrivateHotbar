using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using Slot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using Kind = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace WrathPrivateHotbar;

public sealed unsafe partial class Plugin
{
    private void ResetPrivatePresses()
    {
        privateAttempts.Clear();
        lastAttempt = repeatAttempt = default;
        authoritativeDuring = default;
        castPress = previousCast = default;
        previousHeldMask = previousNativeMask = 0;
        sampledNative = false;
        nativeArt.Reset();
    }

    private void OnPrivatePress(Address source, uint registered, uint resolved)
    {
        if (!interactive || !ready || !(p.Show || p.Pulse || p.Turbo) || privateAttempts.Count >= 128) return;
        var chord = OnBar.Origin == PressOrigin.GameSlot
            ? input.AtDispatch(source, DesktopInput.PhysicalDown) : default;
        privateAttempts.Enqueue(new(source, registered, resolved, job, Environment.TickCount64, chord));
    }

    private void MarkPress(int index, long now, PressSignal signal)
    {
        if (p.Show) nativeArt.Press(index, now, p.PulseMax, signal);
    }

    private int ApplyPrivatePresses(Module* module, long now)
    {
        int latest = -1;
        while (privateAttempts.TryDequeue(out var attempt))
        {
            if (attempt.Job != job || attempt.Source.Bar != p.Bar || now - attempt.Tick is < 0 or > 250) continue;
            int i = attempt.Source.Slot;
            var s = module->GetSlotById((uint)(p.Bar - 1), (uint)i);
            if (s == null || s->CommandId != attempt.Registered) continue;
            if ((releasedSlots & (1 << i)) != 0 && attempt.Chord.Page == 0) continue;
            freshAttempts |= 1 << i;
            if (input.Continues(attempt.Chord))
            {
                if (repeatAttempt.Chord != attempt.Chord) repeatClock.Reset();
                repeatAttempt = attempt;
            }
            MarkPress(i, attempt.Tick, PressSignal.Dispatch);
            lastAttempt = attempt;
            latest = i;
        }
        return latest;
    }

    private bool Own(Module* module, int index)
    {
        var address = new Address(p.Bar, index);
        var slot = module->GetSlotById((uint)(p.Bar - 1), (uint)index);
        if (slot == null || slot->CommandId == 0 ||
            (slot->CommandType != Kind.Action && !GameBars.Sprint(slot->CommandType, slot->CommandId))) return false;
        if (heldFeedback.Source != address || heldFeedback.Registered != slot->CommandId)
        {
            if (heldFeedback.Source.Valid) bars.CancelWaiting(1 << heldFeedback.Source.Slot);
            heldFeedback = new(address, slot->CommandId);
            authoritativeDuring = default;
            lastActionPulse[index] = 0;
        }
        return true;
    }

    private static uint Casting(ActionManager* am)
    {
        if (am == null || am->CastActionType != ActionType.Action || !float.IsFinite(am->CastTimeTotal) ||
            !float.IsFinite(am->CastTimeElapsed) || am->CastTimeTotal <= 0 || am->CastTimeElapsed < 0 ||
            am->CastTimeElapsed >= am->CastTimeTotal) return 0;
        return am->CastActionId != 0 ? am->CastActionId : am->CastSpellId;
    }

    private static bool ValidTarget(ulong id) => id != 0 && id != 0xE0000000;

    // No-target/self actions may report a different recipient to the selected enemy.
    // Keep the real recipient for queue/cast attribution; learn against the selected target.
    private static ulong TargetForPreview(ulong fallback)
    {
        var system = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        ulong selected = system == null ? 0 : system->GetTargetObjectId();
        return ValidTarget(selected) ? selected : fallback;
    }

    private bool HasPress(int slot) => ((contactMask | freshAttempts) & (1 << slot)) != 0;

    private void ObserveCast(Module* module, long now)
    {
        var am = ActionManager.Instance();
        uint cast = Casting(am);
        if (cast == 0)
        {
            if (castPress.Source.Valid && now < castPress.End - 280)
            {
                // Break this partial sequence, retaining confirmed observations for other prefixes.
                sequences.Break();
                castPress = default;
                if (heldFeedback.Source.Valid) heldFeedback = heldFeedback with { PreviewAfter = 0, PreviewId = 0 };
            }
            else if (now > castPress.End + 450) castPress = default;
            return;
        }
        long began = now - (long)(am->CastTimeElapsed * 1000f);
        ulong target = am->CastTargetId;
        if (castPress.Source.Valid && castPress.Target == target && (castPress.Id == cast || castPress.Id == am->CastSpellId) &&
            Math.Abs(castPress.Began - began) <= 200) return;
        var a = lastAttempt;
        if (!a.Source.Valid || a.Source.Bar != p.Bar || Math.Abs(a.Tick - began) > 200 ||
            (a.Resolved != cast && a.Resolved != am->CastSpellId))
        {
            // A repeater can go directly through ActionManager. Bind by a matching held
            // selected button or a recent, attributable use, never by skill ID across bars.
            bool receipt = heldFeedback.Source.Valid && heldFeedback.Source.Bar == p.Bar &&
                Math.Abs(heldFeedback.AcceptedAt - began) <= 230 &&
                (heldFeedback.LastAccepted == cast || heldFeedback.LastAccepted == am->CastSpellId);
            int i = receipt ? heldFeedback.Source.Slot : input.Held;
            if (i < 0 || !receipt && input.StartedAt > began + 180 || view[i].Kind != Kind.Action) return;
            uint id = receipt ? heldFeedback.LastAccepted : view[i].Display;
            if (id == 0 || (id != cast && id != am->CastSpellId)) return;
            a = new(new(p.Bar, i), view[i].Stored, id, job, began, default);
        }
        if (input.Held >= 0 && input.Held != a.Source.Slot) return;
        var slot = module->GetSlotById((uint)(p.Bar - 1), (uint)a.Source.Slot);
        if (slot == null || slot->CommandId != a.Registered || slot->CommandType != Kind.Action) return;
        if (p.Pulse && !Own(module, a.Source.Slot)) return;
        bool reported = heldFeedback.Source == a.Source && heldFeedback.Registered == a.Registered &&
            heldFeedback.LastAccepted == a.Resolved && Math.Abs(heldFeedback.AcceptedAt - began) <= 200;
        authoritativeDuring = default;
        router?.RetireQueued(a.Source, a.Registered, a.Resolved, began);
        if (castPress.ReceiptDue && now <= castPress.End + 450) previousCast = castPress;
        castPress = new(a.Source, a.Registered, a.Resolved, target, began,
            now + (long)((am->CastTimeTotal - am->CastTimeElapsed) * 1000f), !reported, TargetForPreview(target));
        if (!reported && HasPress(a.Source.Slot))
        {
            if (p.Pulse)
            {
                if (heldFeedback.Trying == a.Resolved) bars.CancelWaiting(1 << a.Source.Slot, a.Resolved);
                else PulseTarget(module, a.Source.Slot, slot, a.Resolved, now, true);
            }
            handledAttempts |= 1 << a.Source.Slot;
            heldFeedback = heldFeedback with { Trying = 0, Consumed = a.Resolved, SawLock = true };
        }
    }

    private static bool InGcd(ActionManager* am, uint action, long since)
    {
        if (am == null || action == 0 || since < 0) return false;
        const int globalRecastGroup = 57; // zero-based shared spell/weaponskill recast group
        var gcd = am->GetRecastGroupDetail(globalRecastGroup);
        if (gcd == null || !gcd->IsActive || !float.IsFinite(gcd->Elapsed) || !float.IsFinite(gcd->Total) ||
            gcd->Elapsed < 0 || gcd->Elapsed >= gcd->Total || since > 1000 * gcd->Total + 150) return false;
        return gcd->ActionId == action || am->GetRecastGroup((int)ActionType.Action, action) == globalRecastGroup ||
            am->GetAdditionalRecastGroup(ActionType.Action, action) == globalRecastGroup;
    }

    private void PulseNext(Module* module, int i, long now, bool beat, uint attempted = 0)
    {
        var slot = module->GetSlotById((uint)(p.Bar - 1), (uint)i);
        if (slot == null || slot->CommandId != heldFeedback.Registered) return;
        var am = ActionManager.Instance();
        if (am == null) return;
        bool known = true;
        uint live = GameBars.Sprint(slot->CommandType, slot->CommandId) ? 3 : SelectedAction(i, slot, out known);
        if (!known) live = 0;
        if (live != 0 && slot->CommandType == Kind.Action && view[i].Display != live)
        {
            RefreshSlot(i, slot, am, live);
            typography[i] = typography[i] with { Cost = default, Timer = default };
        }

        uint cast = Casting(am);
        uint accepted = heldFeedback.LastAccepted;
        bool ownCast = cast != 0 && castPress.Source == heldFeedback.Source && castPress.Registered == slot->CommandId &&
            (castPress.Id == cast || castPress.Id == am->CastSpellId);
        uint busy = ownCast ? castPress.Id : cast;
        bool gcd = cast == 0 && InGcd(am, accepted, now - heldFeedback.AcceptedAt);
        if (busy == 0 && accepted != 0 && (gcd ||
            am->AnimationLock > 0 || am->GetActionStatus(ActionType.Action, accepted) != 0))
            busy = accepted;
        uint next = router?.QueuedFor(heldFeedback.Source, slot->CommandId, now) ?? 0;
        string reason = "選択枠の先行入力";
        uint consumed = heldFeedback.Consumed;
        if (consumed != 0)
        {
            if (busy == consumed)
                heldFeedback = heldFeedback with { SawLock = true };
            else if (busy == 0 && heldFeedback.SawLock || live != 0 && live != consumed && live != busy &&
                (cast == 0 || live != cast && live != am->CastSpellId))
                heldFeedback = heldFeedback with { Consumed = 0, SawLock = false };
        }
        bool Eligible(uint id) => id != 0 && id != busy && id != heldFeedback.Consumed &&
            (cast == 0 || id != cast && id != am->CastSpellId);
        if (next == 0 && Eligible(attempted))
        {
            next = attempted;
            reason = "選択枠の押下";
        }
        if (next == 0 && Eligible(live))
        {
            next = live;
            reason = "選択枠の現在候補";
        }
        ulong targetOfAction = ownCast ? castPress.PreviewTarget : heldFeedback.Target;
        var cycle = new Authority(heldFeedback.Source, slot->CommandId, busy, targetOfAction);
        if (busy == 0) authoritativeDuring = default;
        else if (next != 0) authoritativeDuring = cycle;
        if (next == 0 && live != 0 && busy != 0 && p.EarlyPreview && link.Ready && authoritativeDuring != cycle &&
            (cast != 0 ? ownCast || busy == accepted && heldFeedback.Target == TargetForPreview(am->CastTargetId)
                       : gcd))
        {
            ulong target = TargetForPreview(cast != 0 ? (ulong)am->CastTargetId : 0);
            if (target == targetOfAction && (busy == live || cast != 0 && am->CastSpellId == live))
                next = sequences.Predict(new(heldFeedback.Source, slot->CommandId, target), live,
                    live == accepted && (cast == 0 || !ownCast || !castPress.ReceiptDue), now);
            if (next == heldFeedback.Consumed) next = 0;
            reason = "実績からの仮予測";
        }
        if (next == 0)
        {
            previewStatus = busy == 0 ? "次候補待ち" : $"次候補待ち（進行中 ID {busy}）";
            return;
        }
        bool changed = heldFeedback.PreviewId != next;
        if (!beat && !changed) return;
        if (changed) bars.CancelWaiting(1 << i);
        if (PulseTarget(module, i, slot, next, now, true))
        {
            heldFeedback = heldFeedback with { PreviewAfter = busy, PreviewId = next, Trying = next };
            previewStatus = $"{reason} → ID {next}";
            if (changed && cast != 0 && next != busy)
                lastLead = $"{am->CastTimeElapsed / am->CastTimeTotal:P0}　ID {busy} → {next}（{reason}）";
        }
        else
        {
            heldFeedback = heldFeedback with { PreviewAfter = 0, PreviewId = 0 };
            previewStatus = $"{reason} ID {next} の通常枠なし";
        }
    }

    private bool PulseTarget(Module* module, int i, Slot* slot, uint action, long now, bool newPress)
    {
        if (!bars.Pulse(module, p, slot->CommandType, slot->CommandId, action, now, i, newPress)) return false;
        lastActionPulse[i] = now;
        return true;
    }

    private static bool MatchesCast(CastPress cast, ActionUse use) => cast.ReceiptDue && cast.Id == use.Id &&
        cast.Target == use.Target && (!use.Position.Valid || use.Position == cast.Source && use.Stored == cast.Registered) &&
        use.Tick >= cast.Began - 180 && use.Tick <= cast.End + 450;

    private void Drain(Module* m, bool inputAllowed)
    {
        for (int n = 0; n < 256 && uses.TryDequeue(out var e); n++)
        {
            long now = Environment.TickCount64;
            if (e.Job != job || now - e.Tick is < 0 or > 900)
                continue;
            bool currentCastReceipt = MatchesCast(castPress,e);
            bool olderCastReceipt = !currentCastReceipt && MatchesCast(previousCast,e);
            var observedCast = currentCastReceipt ? castPress : previousCast;
            if (!e.Position.Valid && (currentCastReceipt || olderCastReceipt))
                e = e with { Position = observedCast.Source, Stored = observedCast.Registered };
            lastNative = $"{(e.Position.Valid ? e.Position.ToString() : "FF14")} → {e.Id}";
            long sinceReceipt = e.Tick - lastReceiptTime;
            bool duplicate = e.Sequence == lastReceiptSequence && e.Id == lastReceiptAction &&
                sinceReceipt >= 0 && sinceReceipt < (e.Sequence == 0 ? 35 : 350);
            if (duplicate) continue;
            lastReceiptSequence = e.Sequence;
            lastReceiptAction = e.Id;
            lastReceiptTime = e.Tick;
            if (observed.TryGetValue(e.Id, out var old))
                observed[e.Id] = (old.Count + 1, old.Icon, old.Name);
            else if (observed.Count < 400)
            {
                try
                {
                    if (data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(e.Id) is { } row)
                        observed[e.Id] = (1, row.Icon, row.Name.ToString());
                }
                catch (Exception ex)
                {
                    if (lastViewError != ex.GetType()) { lastViewError = ex.GetType(); log.Debug(ex, "Hotbar action details"); }
                }
            }
            // Completion of A may arrive after B has started. Keep the learned order
            // and the current input target anchored to the newer action.
            if (olderCastReceipt) { previousCast = default; continue; }
            if (currentCastReceipt && heldFeedback.Source == castPress.Source &&
                heldFeedback.LastAccepted != 0 && heldFeedback.LastAccepted != e.Id &&
                heldFeedback.AcceptedAt > castPress.Began && heldFeedback.AcceptedAt <= e.Tick)
            {
                castPress = castPress with { ReceiptDue = false };
                continue;
            }
            if (!inputAllowed || e.SelectedBar != p.Bar)
                continue;
            if (e.Position.Valid && e.Position.Bar != p.Bar)
            {
                heldFeedback = default;
                sequences.Break();
                if (p.Pulse) bars.PulseDirect(e.Position.Bar, e.Position, e.Id, now);
                continue;
            }
            // External repeaters may dispatch an action without a fresh physical key edge.
            // Require the selected source or a currently held matching selected action.
            int i = e.Position.Valid ? e.Position.Slot : input.Held;
            if (i < 0)
            {
                sequences.Break();
                continue;
            }
            var s = m->GetSlotById((uint)(p.Bar - 1), (uint)i);
            if (s == null || s->CommandId == 0 ||
                (s->CommandType != Kind.Action && (!GameBars.Sprint(s->CommandType, s->CommandId) || e.Id != 3)) ||
                (e.Position.Valid ? s->CommandId != e.Stored : view[i].Stored != s->CommandId || view[i].Display != e.Id))
            {
                sequences.Break();
                continue;
            }
            var address = new Address(p.Bar, i);
            if (p.EarlyPreview) sequences.Accept(new(address, s->CommandId,
                castPress.Source == address && castPress.Id == e.Id && castPress.Target == e.Target &&
                e.Tick >= castPress.Began - 180 && e.Tick <= castPress.End + 450 ? castPress.PreviewTarget : e.PreviewTarget), e.Id, e.Tick);
            bool forecast = heldFeedback.Source == address && heldFeedback.Registered == s->CommandId && heldFeedback.Trying == e.Id;
            if (forecast) bars.CancelWaiting(1 << i, e.Id);
            if (lastAttempt.Source == address && lastAttempt.Resolved == e.Id) handledAttempts |= 1 << i;
            bool advanced = heldFeedback.Source == address && heldFeedback.Registered == s->CommandId &&
                heldFeedback.PreviewAfter == e.Id && heldFeedback.PreviewId != 0 && heldFeedback.PreviewId != e.Id;
            if (!advanced && authoritativeDuring.Running != e.Id) authoritativeDuring = default;
            if (input.Held == i || (input.Held < 0 && e.Position.Valid))
            {
                Own(m, i);
                heldFeedback = heldFeedback with { LastAccepted = e.Id, Target = e.PreviewTarget, AcceptedAt = now,
                    PreviewAfter = advanced ? e.Id : 0, PreviewId = advanced ? heldFeedback.PreviewId : e.Id,
                    Trying = advanced ? heldFeedback.Trying : 0, Consumed = e.Id,
                    SawLock = heldFeedback.Consumed == e.Id && heldFeedback.SawLock };
            }
            if (currentCastReceipt) castPress = castPress with { ReceiptDue = false };
            // Some casts report acceptance only after the next button is already being shown.
            // Do not replay the old slot or leave a late, unpaired button sound.
            if (p.Pulse && HasPress(i) && !currentCastReceipt && !advanced && !forecast)
                PulseTarget(m, i, s, e.Id, now, (freshAttempts & (1 << i)) != 0);
        }
    }
}
