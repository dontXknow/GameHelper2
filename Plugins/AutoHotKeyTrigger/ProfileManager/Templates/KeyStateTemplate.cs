// <copyright file="KeyStateTemplate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.Templates
{
    using ClickableTransparentOverlay.Win32;
    using GameHelper.Utils;
    using ImGuiNET;

    /// <summary>
    ///     ImGui widget for checking whether a key is currently held down.
    /// </summary>
    public static class KeyStateTemplate
    {
        private static VK selectedKey = VK.KEY_W;
        private static bool shouldBeDown;

        /// <summary>
        ///     Displays the key-state condition template.
        /// </summary>
        /// <returns>The generated condition, or an empty string when it was not added.</returns>
        public static string Add()
        {
            ImGui.Text(AhkText.T("template.key", "Key"));
            ImGui.SameLine();
            ImGuiHelper.NonContinuousEnumComboBox(
                AhkText.Label("rule.key", "Key", "KeyStateTemplate"),
                ref selectedKey);
            ImGui.SameLine();
            ImGui.Checkbox(
                AhkText.Label("template.key_is_down", "held down", "KeyShouldBeDown"),
                ref shouldBeDown);
            ImGui.SameLine();
            ImGui.Text(shouldBeDown
                ? AhkText.T("template.key_held", "is held down")
                : AhkText.T("template.key_not_held", "is not held down"));
            ImGui.SameLine();
            if (!ImGui.Button(AhkText.Label("button.add", "Add", "KeyState")))
            {
                return string.Empty;
            }

            var expression = $"IsKeyDownForAction(VK.{selectedKey})";
            return shouldBeDown ? expression : $"!{expression}";
        }
    }
}
