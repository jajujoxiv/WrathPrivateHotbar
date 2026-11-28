using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace WrathPrivateHotbar;

public sealed unsafe partial class Plugin
{
    private bool dragging;
    private int pressedSlot = -1, pressedBar;
    private uint pressedId;
    private FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType pressedKind;
    private void Draw()
    {
        textEntry = ImGui.GetIO().WantTextInput;
        if (settings) DrawOptions();
        if (!interactive || !p.Show) return;

        var vp = ImGui.GetMainViewport();
        if (vp.WorkSize.X < 1 || vp.WorkSize.Y < 1) return;
        long now = Environment.TickCount64;
        bool populated = Array.Exists(view, c => c.Stored != 0);
        var pad = ImGui.GetStyle().WindowPadding;
        var available = Vector2.Max(Vector2.One, vp.WorkSize - pad * 2);
        int columns = p.Columns, rows = (12 + columns - 1) / columns;
        // Keep a fractional pitch. Rounding each gap would lose a 0.1px change in all twelve slots.
        float gutterX = p.ColumnGap / 48f, gutterY = p.RowGap / 48f;
        float fitX = available.X / (columns + (columns - 1) * gutterX + .66f);
        float fitY = available.Y / (rows + (rows - 1) * gutterY + .36f);
        int size = Math.Max(1, (int)MathF.Min(p.Size, MathF.Min(fitX, fitY)));
        float gapX = size * gutterX, gapY = size * gutterY;
        var pitch = new Vector2(size + gapX, size + gapY);
        float baseTop = size * .28f;
        float width = columns * size + (columns - 1) * gapX;
        float lockGap = size * .12f, lockSize = size * .54f;
        var extent = Vector2.Min(vp.WorkSize, new Vector2(width + lockGap + lockSize,
            baseTop + rows * size + (rows - 1) * gapY + size * .08f) + pad * 2);
        var room = Vector2.Max(Vector2.Zero, vp.WorkSize - extent);
        var rel = p.X < 0 || p.Y < 0 ? room * .5f : Vector2.Clamp(new(p.X, p.Y), Vector2.Zero, room);
        var anchor = vp.WorkPos + rel;
        anchor = new(MathF.Round(anchor.X), MathF.Round(anchor.Y));
        // Expand the draw area around the saved icon position, even for fractional key shifts.
        var before = Vector2.Min(Vector2.Max(Vector2.Zero, anchor - vp.WorkPos),
            new(MathF.Ceiling(MathF.Max(0, -p.KeyOffsetX) * size / 48f), MathF.Ceiling(MathF.Max(0, -p.KeyOffsetY) * size / 48f)));
        ImGui.SetNextWindowPos(anchor - before, ImGuiCond.Always);
        ImGui.SetNextWindowSize(extent + before, ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        bool open = ImGui.Begin("###WPHVirtualBar", flags);
        try
        {
            if (!open) return;
            var dl = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos() + before + new Vector2(0, baseTop);
            Span<float> contacts = stackalloc float[12];
            for (int i = 0; i < 12; i++)
            {
                var min = origin + new Vector2(i % columns * pitch.X, i / columns * pitch.Y);
                var max = min + new Vector2(size);
                var cell = view[i];
                if (cell.Stored == 0) continue;
                ImGui.SetCursorScreenPos(min);
                ImGui.PushID(i);
                ImGui.InvisibleButton("slot", new Vector2(size));
                if (ready && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    releasedSlots &= ~(1 << i);
                    pressedBar = p.Bar; pressedSlot = i;
                    pressedId = cell.Stored; pressedKind = cell.Kind;
                    MarkPress(i, now, PressSignal.Contact);
                }
                if (ImGui.IsItemActive() && !p.Locked && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    MoveBar(rel, room);
                    pressedSlot = -1;
                }
                if (ImGui.IsItemDeactivated() && pressedSlot == i)
                {
                    if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    {
                        clickedBar = pressedBar; clickedId = pressedId; clickedKind = pressedKind; clicked = i;
                    }
                    pressedSlot = -1;
                }
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) settings = true;
                ImGui.PopID();
                bool held = input.PrivateKeyDown(i, DesktopInput.PhysicalDown) ||
                    pressedSlot == i && pressedBar == p.Bar && pressedId == cell.Stored && pressedKind == cell.Kind;
                nativeArt.ContactState(i, held, now);
                contacts[i] = DrawSlot(dl, cell, i, min, max, size, gapY, now);
            }
            // A deliberately offset key can straddle two slots. Draw it after all icon faces.
            for (int i = 0; i < 12; i++)
            {
                if (view[i].Stored == 0) continue;
                var min = origin + new Vector2(i % columns * pitch.X, i / columns * pitch.Y);
                DrawKeyText(dl, i, min, min + new Vector2(size), size, gapY, contacts[i]);
            }
            if (!ready || !populated)
                Text(dl, origin + new Vector2(2, size * .28f), ready ? "未登録" : "読み込み中", 0xFFFFFFFF);

            var lockPos = origin + new Vector2(width + lockGap, (size - lockSize) * .5f);
            ImGui.SetCursorScreenPos(lockPos);
            ImGui.InvisibleButton("##WPHLock", new Vector2(lockSize));
            bool movedLock = !p.Locked && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
            if (movedLock) MoveBar(rel, room);
            if (ImGui.IsItemDeactivated() && ImGui.IsItemHovered() && !dragging && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                p.Locked = !p.Locked;
                Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(p.Locked ? "クリックで位置のロックを解除" : "クリックで位置を固定／ドラッグで移動");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) settings = true;
            }
            DrawLock(dl, lockPos, lockSize, p.Locked, ImGui.IsItemHovered());
            if (dragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) { dragging = false; Save(); }
        }
        finally { ImGui.End(); }
    }

    private float DrawSlot(ImDrawListPtr dl, SlotView cell, int index, Vector2 min, Vector2 max, int size, float gap, long now)
    {
        float radius = MathF.Max(1, size * .087f);
        var inset = new Vector2(MathF.Max(.5f, size * .021f));
        var imageMin = min + inset;
        var imageMax = max - inset;
        dl.AddRectFilled(min, max, 0xF21C1B19, radius);
        int firstIconVertex = dl.VtxBuffer.Size, iconVertexCount = 0;
        uint iconId = cell.Icon;
        if (iconId != 0 &&
            (textures.GetFromGameIcon(new GameIconLookup { IconId = iconId, HiRes = true }).TryGetWrap(out var icon, out _) ||
             textures.GetFromGameIcon(new GameIconLookup { IconId = iconId, HiRes = false }).TryGetWrap(out icon, out _)) && icon != null)
        {
            dl.AddImageRounded(icon.Handle, imageMin, imageMax, Vector2.Zero, Vector2.One, 0xFFFFFFFF, radius);
            iconVertexCount = dl.VtxBuffer.Size - firstIconVertex;
        }
        else
        {
            string number = (index + 1).ToString();
            Text(dl, (min + max - ImGui.CalcTextSize(number)) * .5f, number, 0xFFCFCFCF);
        }
        dl.AddRect(min + new Vector2(.5f), max - new Vector2(.5f), 0xB6999387, radius, ImDrawFlags.None, MathF.Max(1, size * .021f));
        if (cell.Remaining > 0 && cell.Total > 0)
        {
            float fraction = Math.Clamp(cell.Remaining / cell.Total, 0, 1);
            var center = (min + max) * .5f;
            float r = size * .45f;
            int segments = Math.Max(1, (int)MathF.Ceiling(fraction * 32));
            var previous = center - new Vector2(0, r);
            for (int seg = 1; seg <= segments; seg++)
            {
                float angle = -MathF.PI * .5f + MathF.Tau * fraction * seg / segments;
                var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
                dl.AddTriangleFilled(center, previous, next, 0x68211B15);
                previous = next;
            }
        }
        if (cell.Highlight)
            dl.AddRect(min + new Vector2(1), max - new Vector2(1), 0xE376DAFD, radius, ImDrawFlags.None, MathF.Max(1, size * .028f));
        // Only this window receives the overlay. Cooldown darkening is below it and captions stay above.
        float contact = nativeArt.ContactLight(index, now);
        dl.PushClipRect(min, max, true);
        try
        {
            if (contact > 0)
            {
                if (!nativeArt.Draw(index, dl, min, max, radius, now, contact))
                    pressSurface.Draw(dl, min, max, radius, contact, now, firstIconVertex, iconVertexCount);
            }
        }
        finally { dl.PopClipRect(); }
        DrawSlotText(dl, cell, index, min, max, size, gap);
        return contact;
    }

    private static uint WithAlpha(uint rgb, float alpha) =>
        (rgb & 0x00FFFFFF) | (uint)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255) << 24;

    private static void DrawLock(ImDrawListPtr dl, Vector2 pos, float side, bool locked, bool hovered)
    {
        Vector2 P(float x, float y) => pos + new Vector2(x, y) * side;
        uint ink = hovered ? 0xFFFFFFFF : 0xFFE5E2DD;
        var offset = new Vector2(side * .04f, side * .05f);
        float left = locked ? .29f : .43f;
        float right = left + .38f;
        for (int pass = 0; pass < 2; pass++)
        {
            var delta = pass == 0 ? offset : Vector2.Zero;
            uint col = pass == 0 ? 0xC5201E1C : ink;
            dl.PathLineTo(P(left, .53f) + delta);
            dl.PathArcTo(P(left + .19f, .35f) + delta, side * .19f, MathF.PI, MathF.Tau, 14);
            dl.PathLineTo(P(right, locked ? .53f : .37f) + delta);
            dl.PathStroke(col, ImDrawFlags.None, MathF.Max(1, side * .095f));
            dl.AddRectFilled(P(.18f, .48f) + delta, P(.79f, .87f) + delta, col, side * .095f);
        }
        dl.AddCircleFilled(P(.49f, .65f), MathF.Max(1, side * .065f), 0xFF47433E, 12);
    }

    private void MoveBar(Vector2 current, Vector2 room)
    {
        var position = Vector2.Clamp(current + ImGui.GetIO().MouseDelta, Vector2.Zero, room);
        p.X = position.X;
        p.Y = position.Y;
        dragging = true;
    }
    private static void Text(ImDrawListPtr dl, Vector2 pos, string value, uint color)
    {
        if (value.Length != 0)
        {
            dl.AddText(pos + Vector2.One, 0xFF151515, value);
            dl.AddText(pos, color, value);
        }
    }
    private void Toggle(string label, bool value, Action<bool> update)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            update(value);
            Save();
        }
    }
    private static readonly string[] KeyAnchors = ["左上", "中央上", "右上"];
    private static readonly string[] NumberFontNames = ["自動（純正）", "AXIS", "Miedinger", "Trump Gothic"];

    private void DrawNumberStyle(string label, NumberStyle style)
    {
        if (ImGui.BeginCombo(label + "のフォント", NumberFontNames[(int)style.Font]))
        {
            for (int i = 0; i < NumberFontNames.Length; i++)
                if (ImGui.Selectable(NumberFontNames[i] + "##" + label, (int)style.Font == i))
                { style.Font = (NumberFont)i; Save(); }
            ImGui.EndCombo();
        }
        int size = style.Percent;
        if (ImGui.SliderInt(label + "の文字サイズ (%)", ref size, 50, 180)) { style.Percent = size; Save(); }
    }
    
    private void DrawOptions()
    {
        ImGui.SetNextWindowSize(new Vector2(535, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("WrathPrivateHotbar 2.0.10.0###WPHConfig", ref settings))
        {
            if (!status.StartsWith(link.Status, StringComparison.Ordinal))
                ImGui.TextWrapped(link.Status);
            ImGui.TextWrapped(status);
            if (ready && usedHook == null)
                ImGui.TextWrapped("スキル使用の通知を待機中です。通知がつながると、使用時の発光とSEが有効になります。");
            if (router?.LastError is { Length: > 0 } msg)
                ImGui.TextWrapped("選択HBの照合エラー　" + msg);
            if (saveError != "")
                ImGui.TextWrapped(saveError);
            Toggle("疑似ホットバーを表示", p.Show, v => p.Show = v);
            ImGui.TextWrapped("選択HBだけでWrathComboを使います。使用中はWrathComboの全体置換を一時停止します。");
            if (ImGui.BeginCombo("WrathCombo用ホットバー", $"HB{p.Bar}"))
            {
                for (int i = 1; i <= 10; i++)
                    if (ImGui.Selectable($"HB{i}", p.Bar == i) && p.Bar != i)
                    {
                        p.Bar = i;
                        ResetInteraction();
                        Array.Clear(view);
                        Array.Clear(keys);
                        Array.Clear(nativeLabels);
                        scanAt = auditScanAt = 0;
                        Save();
                    }
                ImGui.EndCombo();
            }
            ImGui.TextWrapped("通常HBの発光先（複数選択可）。複数一致は小さい番号を優先し、指定先に該当スキルがなければほかの表示中のHBを使います。");
            for (int i = 1; i <= 10; i++)
            {
                bool active = (p.PreferredBars & (1 << (i - 1))) != 0;
                ImGui.BeginDisabled(i == p.Bar);
                if (ImGui.Checkbox($"HB{i}", ref active))
                {
                    p.PreferredBars ^= 1 << (i - 1);
                    Save();
                }
                ImGui.EndDisabled();
                if (i % 5 != 0)
                    ImGui.SameLine();
            }
            Toggle("対応する通常HBの枠を発光させる", p.Pulse, v => { p.Pulse = v; bars.Reset(); });
            Toggle("純正HBの発光1回につきSEを1回鳴らす", p.Sound, v => p.Sound = v);
            Toggle("隠した元HBの押下SEを消す", p.MuteSourceClick, v => p.MuteSourceClick = v);
            Toggle("元の選択HBを隠す", p.HideOriginal, v => p.HideOriginal = v);
            if (ImGui.CollapsingHeader("配置・連打"))
            {
                Toggle("位置を固定（右クリックで設定）", p.Locked, v => p.Locked = v);
                int x = (int)p.X, y = (int)p.Y;
                var screen = ImGui.GetMainViewport().Size;
                if (ImGui.SliderInt("横位置", ref x, 0, (int)screen.X))
                {
                    p.X = x;
                    Save();
                }
                if (ImGui.SliderInt("縦位置", ref y, 0, (int)screen.Y))
                {
                    p.Y = y;
                    Save();
                }
                int size = p.Size, col = p.Columns;
                float gx = p.ColumnGap, gy = p.RowGap;
                if (ImGui.InputFloat("横の枠間隔（48px基準）", ref gx, .1f, 1f, "%.1f"))
                {
                    p.ColumnGap = gx;
                    Save();
                }
                if (ImGui.InputFloat("縦の枠間隔（48px基準）", ref gy, .1f, 1f, "%.1f"))
                {
                    p.RowGap = gy;
                    Save();
                }
                int timerText = p.TimerTextPercent, keyText = p.KeyTextPercent;
                if (ImGui.SliderInt("リキャスト文字サイズ (%)", ref timerText, 65, 140))
                {
                    p.TimerTextPercent = timerText;
                    Save();
                }
                if (ImGui.SliderInt("キー文字サイズ (%)", ref keyText, 65, 140))
                {
                    p.KeyTextPercent = keyText;
                    Save();
                }
                if (ImGui.BeginCombo("キー文字の基準位置", KeyAnchors[(int)p.KeyAlign]))
                {
                    for (int i = 0; i < KeyAnchors.Length; i++)
                        if (ImGui.Selectable(KeyAnchors[i], (int)p.KeyAlign == i)) { p.KeyAlign = (KeyAlignment)i; Save(); }
                    ImGui.EndCombo();
                }
                float dx = p.KeyOffsetX, dy = p.KeyOffsetY;
                if (ImGui.InputFloat("キー文字の横位置（48px基準）", ref dx, .1f, 1f, "%.1f")) { p.KeyOffsetX = dx; Save(); }
                if (ImGui.InputFloat("キー文字の縦位置（48px基準）", ref dy, .1f, 1f, "%.1f")) { p.KeyOffsetY = dy; Save(); }
                DrawNumberStyle("消費MPなど", p.ResourceText);
                DrawNumberStyle("アイテム所持数", p.StockText);
                DrawNumberStyle("チャージ数", p.ChargeText);
                if (ImGui.SliderInt("アイコンサイズ", ref size, 24, 120))
                {
                    p.Size = size;
                    Save();
                }
                if (ImGui.SliderInt("1行の枠数", ref col, 1, 12))
                {
                    p.Columns = col;
                    Save();
                }
                Toggle("リキャストを表示", p.ShowCooldown, v => p.ShowCooldown = v);
                Toggle("詠唱・GCD中の技順の先読み", p.EarlyPreview, v => { p.EarlyPreview = v; sequences.Reset(); });
                ImGui.TextWrapped("同じ対象・選択枠で直前2技と次の技が3回一致すると、詠唱の早い段階から通常枠に仮表示します。実際の先行入力とWrathComboの次候補を優先します。");
                int low = p.PulseMin, high = p.PulseMax;
                if (ImGui.SliderInt("最短発光間隔 (ms)", ref low, 60, 999))
                {
                    p.PulseMin = low;
                    Save();
                }
                if (ImGui.SliderInt("最長発光間隔 (ms)", ref high, p.PulseMin + 1, 1000))
                {
                    p.PulseMax = high;
                    Save();
                }
                Toggle("内蔵の長押し連打", p.Turbo, v => p.Turbo = v);
                if (externalRepeat)
                    ImGui.TextWrapped("ReActionを検出しました。連打はReActionが担当します。");
                if (p.Turbo && !externalRepeat)
                {
                    int interval = p.RepeatMs;
                    if (ImGui.SliderInt("連打間隔 (ms)", ref interval, 60, 500))
                    {
                        p.RepeatMs = interval;
                        Save();
                    }
                    Toggle("戦闘外でも内蔵連打を使う", p.RepeatOutOfCombat, v => p.RepeatOutOfCombat = v);
                }
            }
            if (ImGui.CollapsingHeader("未登録スキルの候補"))
            {
                ImGui.TextWrapped("このジョブでクライアントが受け付けたスキルのうち、選択HB以外で登録・標準変化を確認できなかった候補です。ジョブ変更でクリアします。");
                foreach (var (id, entry) in observed)
                {
                    if (!absentSince.TryGetValue(id, out var since) || Environment.TickCount64 - since < 1500)
                        continue;
                    if (entry.Icon != 0 && textures.GetFromGameIcon(new GameIconLookup { IconId = entry.Icon }).TryGetWrap(out var tex, out _) && tex != null)
                    {
                        ImGui.Image(tex.Handle, new Vector2(22));
                        ImGui.SameLine();
                    }
                    ImGui.Text($"{entry.Name}  ID {id}  {entry.Count}回");
                }
            }
            if (ImGui.CollapsingHeader("トラブル時の診断"))
            {
                ImGui.TextWrapped($"直近の発光先　{bars.Last}　要求 {bars.Requests}／描画確認 {bars.Drawn}／SE {bars.Clicks}");
                ImGui.TextWrapped(nativeEvents.Status);
                ImGui.TextWrapped($"連打の判定　{previewStatus}　／　疑似枠　{nativeArt.Last}");
                if (nativeArt.LastSlot >= 0)
                    ImGui.TextWrapped($"疑似HBの直近の入力　HB{p.Bar}の{nativeArt.LastSlot + 1}枠目　／　累計の入力通知 {nativeArt.Inputs}回　押下描画の開始 {nativeArt.Onsets}回");
                ImGui.TextWrapped($"直近の詠唱中の切り替え　{lastLead}");
                ImGui.TextWrapped($"直近のクライアント実行　{lastNative}");
                ImGui.TextWrapped($"直近の迅速魔　{Volatile.Read(ref lastSwiftcast)}");
            }
        }
        ImGui.End();
    }
}
