// <copyright file="BarrageTemplate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.Templates
{
    using System;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions;
    using GameHelper;
    using ImGuiNET;

    /// <summary>
    ///     ImGui widget for smart Barrage activation conditions.
    /// </summary>
    public static class BarrageTemplate
    {
        private static int minimumFrenzyCharges = 1;

        /// <summary>
        ///     Displays the smart Barrage condition template.
        /// </summary>
        /// <returns>The generated condition, or an empty string when it was not added.</returns>
        public static string Add()
        {
            ImGui.Text(AhkText.T("template.barrage_smart", "Smart Barrage"));
            ImGui.TextWrapped(AhkText.T(
                "template.barrage_smart.help",
                "Use Barrage when it is ready and no empowerment is active, or replace the active empowerment when more Frenzy Charges are available than were used for it."));

            ImGui.Text(AhkText.T("template.minimum_frenzy_charges", "Minimum Frenzy Charges"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
            ImGui.DragInt("##BarrageMinimumFrenzyCharges", ref minimumFrenzyCharges, 0.1f, 0, 20);

            DrawCurrentValues();

            ImGui.SameLine();
            if (!ImGui.Button(AhkText.Label("button.add", "Add", "SmartBarrage")))
            {
                return string.Empty;
            }

            return $"ShouldUseBarrage({minimumFrenzyCharges})";
        }

        private static void DrawCurrentValues()
        {
            try
            {
                var state = new DynamicConditionState(Core.States.InGameStateObject);
                ImGui.Separator();
                ImGui.Text(AhkText.T("template.current_barrage_values", "Current Barrage values"));
                ImGui.Text($"{AhkText.T("template.barrage_active", "Empowerment active")}: {state.BarrageIsEmpowered}");
                ImGui.Text($"{AhkText.T("template.barrage_ready", "Cooldown ready")}: {state.BarrageIsUsable}");
                ImGui.Text($"{AhkText.T("template.current_frenzy_charges", "Current Frenzy Charges")}: {state.BarrageCurrentFrenzyCharges}");
                ImGui.Text($"{AhkText.T("template.empowered_frenzy_charges", "Charges used for active empowerment")}: {state.BarrageEmpoweredFrenzyCharges}");
                ImGui.Text($"{AhkText.T("template.barrage_upgrade", "Stronger recast available")}: {state.BarrageUpgradeAvailable}");
            }
            catch (NullReferenceException)
            {
                ImGui.TextDisabled(AhkText.T(
                    "template.barrage_no_player",
                    "Enter the game to display the current Barrage values."));
            }
        }
    }
}
