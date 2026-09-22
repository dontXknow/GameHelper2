// <copyright file="MovementTemplate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.Templates
{
    using System.Globalization;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions;
    using ImGuiNET;

    /// <summary>
    ///     ImGui widget for movement-state conditions.
    /// </summary>
    public static class MovementTemplate
    {
        private static bool shouldBeMoving;
        private static float minimumStationarySeconds;

        /// <summary>
        ///     Displays the movement condition template.
        /// </summary>
        /// <returns>The generated condition, or an empty string when it was not added.</returns>
        public static string Add()
        {
            ImGui.Text(AhkText.T("template.player_movement", "Player movement"));
            ImGui.SameLine();
            ImGui.Checkbox(
                AhkText.Label("template.moving", "moving", "PlayerIsMoving"),
                ref shouldBeMoving);
            ImGui.SameLine();
            ImGui.Text(shouldBeMoving
                ? AhkText.T("template.is_moving", "is moving")
                : AhkText.T("template.is_not_moving", "is not moving"));
            if (!shouldBeMoving)
            {
                ImGui.Text(AhkText.T("template.stationary_for", "continuously for at least"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
                ImGui.DragFloat(
                    AhkText.Label("component.wait.seconds", "seconds", "MovementStationarySeconds"),
                    ref minimumStationarySeconds,
                    0.05f,
                    0,
                    30);
            }

            ImGui.SameLine();
            if (!ImGui.Button(AhkText.Label("button.add", "Add", "Movement")))
            {
                return string.Empty;
            }

            return shouldBeMoving
                ? "PlayerIsMoving"
                : $"!PlayerIsMoving && PlayerStationarySeconds >= {minimumStationarySeconds.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}
