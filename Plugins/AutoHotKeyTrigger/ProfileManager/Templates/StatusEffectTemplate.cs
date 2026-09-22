// <copyright file="StatusEffectTemplate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.Templates
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions;
    using AutoHotKeyTrigger.ProfileManager.Enums;
    using GameHelper;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Objects.Components;
    using ImGuiNET;

    /// <summary>
    ///     ImGui widget that helps user modify the condition code in <see cref="DynamicCondition"/>.
    /// </summary>
    public static class StatusEffectTemplate
    {
        private static readonly List<string> SupportedOperatorTypes = new()
        {
            "has",
            "not has",
            ">",
            ">=",
            "<",
            "<="
        };

        private static readonly HashSet<int> BuffCategories = new()
        {
            1,  // Buff
            3,  // Charge
            4,  // Flask
            6,  // Active skill
            7,  // Buff shrine
            8,  // PVP flag
            9,  // Spell shrine
            10, // PVP team
            12, // Aspect
            13, // Herald
            15, // Stolen
            16, // Link
            17, // Charm (PoE 2) / Tincture (PoE 1)
        };

        private static readonly HashSet<int> DebuffCategories = new()
        {
            2,  // Debuff
            5,  // Hex
            11, // Labyrinth trap
            14, // Mark
        };

        private static string selectedBuffId = "grace_period";
        private static string selectedDebuffId = "bleeding";
        private static string selectedOperator = "has";
        private static StatusEffectCheckType checkType = StatusEffectCheckType.PercentTimeLeft;
        private static float threshold;

        /// <summary>
        ///     Status-effect group displayed by the template.
        /// </summary>
        public enum Filter
        {
            Buff,
            Debuff,
        }

        /// <summary>
        ///     Display the ImGui widget for adding the condition in <see cref="DynamicCondition"/>.
        /// </summary>
        /// <param name="filter">Whether to show positive or negative status effects.</param>
        /// <returns>
        ///     condition in string format if user press Add button otherwise empty string.
        /// </returns>
        public static string Add(Filter filter)
        {
            var effectId = filter == Filter.Buff ? selectedBuffId : selectedDebuffId;
            var buffs = GetPlayerBuffs();

            ImGui.PushID($"StatusEffectDuration{filter}");
            DrawActiveEffectPicker(filter, buffs, ref effectId);
            if (selectedOperator == "has" || selectedOperator == "not has")
            {
                ImGui.Text(AhkText.T("template.player", "Player"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 4);
                ImGuiHelper.IEnumerableComboBox("##StatusEffectOperator", SupportedOperatorTypes, ref selectedOperator);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
                ImGui.InputText(GetEffectInputLabel(filter), ref effectId, 200);
                HelpBox();
            }
            else
            {
                ImGui.Text(GetPlayerHasText(filter));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
                ImGui.InputText(AhkText.Label("template.with", "with", "StatusEffectWith"), ref effectId, 200);
                HelpBox();
                ImGui.SameLine();
                ImGuiHelper.IEnumerableComboBox("##StatusEffectOperator", SupportedOperatorTypes, ref selectedOperator);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
                ImGui.InputFloat("##threshold", ref threshold);
                ImGui.SameLine();
                ImGuiHelper.EnumComboBox("##checkType", ref checkType);
                ImGuiHelper.ToolTip(AhkText.F(
                    "template.status_compare.tooltip",
                    "What to compare. {0} ranges from 0 to 100, 0 being the effect will expire imminently and 100 meaning it was just applied",
                    StatusEffectCheckType.PercentTimeLeft));
            }

            DrawCurrentValues(buffs, effectId);
            SetSelectedEffectId(filter, effectId);

            ImGui.PopID();
            ImGui.SameLine();
            if (!ImGui.Button(AhkText.Label("button.add", "Add", $"StatusEffect{filter}")))
            {
                return string.Empty;
            }

            var escapedEffectId = EscapeStringLiteral(effectId);
            return selectedOperator switch
            {
                "has" => $"PlayerBuffs.Has(\"{escapedEffectId}\")",
                "not has" => $"!PlayerBuffs.Has(\"{escapedEffectId}\")",
                _ => $"PlayerBuffs[\"{escapedEffectId}\"].{checkType} {selectedOperator} {threshold.ToString(CultureInfo.InvariantCulture)}",
            };
        }

        private static Buffs? GetPlayerBuffs()
        {
            var player = Core.States?.InGameStateObject?.CurrentAreaInstance?.Player;
            return player != null && player.TryGetComponent<Buffs>(out var buffs) ? buffs : null;
        }

        private static void DrawActiveEffectPicker(Filter filter, Buffs? buffs, ref string effectId)
        {
            var names = buffs?.StatusEffects.Keys
                .Where(name => buffs.StatusEffectCategories.TryGetValue(name, out var category) &&
                               IsCategoryIncluded(filter, category))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();

            var label = filter == Filter.Buff
                ? AhkText.Label("template.active_buffs", "Active buffs", "ActiveEffectPicker")
                : AhkText.Label("template.active_debuffs", "Active debuffs", "ActiveEffectPicker");
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 24);
            ImGui.BeginDisabled(names.Length == 0);
            ImGuiHelper.IEnumerableComboBox(label, names, ref effectId);
            ImGui.EndDisabled();
            if (names.Length == 0)
            {
                ImGui.TextWrapped(filter == Filter.Buff
                    ? AhkText.T(
                        "template.no_active_buffs",
                        "No active buffs available. You can enter a buff name manually below.")
                    : AhkText.T(
                        "template.no_active_debuffs",
                        "No active debuffs available. You can enter a debuff name manually below."));
            }
        }

        private static void DrawCurrentValues(Buffs? buffs, string effectId)
        {
            if (buffs == null || !buffs.StatusEffects.TryGetValue(effectId, out var effect))
            {
                return;
            }

            var percentTimeLeft = CalculatePercentTimeLeft(effect);
            ImGui.Separator();
            ImGui.Text(AhkText.T("template.current_status_values", "Current values (usable rule options)"));
            ImGui.Text("Exists: true");
            if (buffs.StatusEffectCategories.TryGetValue(effectId, out var category))
            {
                ImGui.Text($"Category: {GetCategoryName(category)} ({category})");
            }

            ImGui.Text($"Charges: {effect.Charges}");
            ImGui.Text($"TimeLeft: {FormatDuration(effect.TimeLeft)}");
            ImGui.Text($"TotalTime: {FormatDuration(effect.TotalTime)}");
            ImGui.Text($"PercentTimeLeft: {percentTimeLeft.ToString("0.###", CultureInfo.CurrentCulture)}");
            ImGui.Text($"Effectiveness: {effect.Effectiveness} ({AhkText.T("template.display_value", "display value")}: {100 + effect.Effectiveness})");
        }

        private static double CalculatePercentTimeLeft(StatusEffectStruct effect)
        {
            if (float.IsPositiveInfinity(effect.TimeLeft))
            {
                return 100;
            }

            return effect.TotalTime > 0 ? 100 * (effect.TimeLeft / effect.TotalTime) : 0;
        }

        private static string FormatDuration(float value) =>
            float.IsPositiveInfinity(value) ? "∞" : value.ToString("0.###", CultureInfo.CurrentCulture);

        private static bool IsCategoryIncluded(Filter filter, int category) =>
            filter == Filter.Buff ? BuffCategories.Contains(category) : DebuffCategories.Contains(category);

        private static string GetCategoryName(int category) => category switch
        {
            1 => "Buff",
            2 => "Debuff",
            3 => "Charge",
            4 => "Flask",
            5 => "Hex",
            6 => "Active skill",
            7 => "Buff shrine",
            8 => "PVP flag",
            9 => "Spell shrine",
            10 => "PVP team",
            11 => "Labyrinth trap",
            12 => "Aspect",
            13 => "Herald",
            14 => "Mark",
            15 => "Stolen",
            16 => "Link",
            17 => "Charm/Tincture",
            _ => "Unknown",
        };

        private static string GetEffectInputLabel(Filter filter) => filter == Filter.Buff
            ? AhkText.Label("template.buff", "buff", "StatusEffectBuff")
            : AhkText.Label("template.debuff", "debuff", "StatusEffectDebuff");

        private static string GetPlayerHasText(Filter filter) => filter == Filter.Buff
            ? AhkText.T("template.player_has_buff", "Player has buff")
            : AhkText.T("template.player_has_debuff", "Player has debuff");

        private static void SetSelectedEffectId(Filter filter, string effectId)
        {
            if (filter == Filter.Buff)
            {
                selectedBuffId = effectId;
            }
            else
            {
                selectedDebuffId = effectId;
            }
        }

        private static string EscapeStringLiteral(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                 .Replace("\"", "\\\"", StringComparison.Ordinal);

        private static void HelpBox()
        {
            ImGuiHelper.ToolTip(AhkText.T(
                "template.status_effect.tooltip",
                "Select a currently active effect above or enter its internal name manually. The current values show which comparisons are available."));
        }
    }
}
