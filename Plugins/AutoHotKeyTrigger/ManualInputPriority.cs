// <copyright file="ManualInputPriority.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    ///     Detects physical keyboard and mouse-button input so user actions can take priority over
    ///     normal automatic profile rules.
    /// </summary>
    internal static class ManualInputPriority
    {
        private const int FirstVirtualKey = 0x01;
        private const int LastVirtualKey = 0xFE;
        private static DateTime lastPhysicalInputAt = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        /// <summary>
        ///     Gets a value indicating whether normal rules should currently remain paused.
        ///     AutoHotKeyTrigger sends actions directly as window messages, so those automatic
        ///     actions do not appear as physically held keys here.
        /// </summary>
        /// <param name="graceSeconds">How long to keep pausing after physical input is released.</param>
        /// <returns>True while a physical key/button is held or the grace period is active.</returns>
        internal static bool ShouldPause(float graceSeconds)
        {
            var now = DateTime.UtcNow;
            if (IsAnyPhysicalInputDown())
            {
                lastPhysicalInputAt = now;
                return true;
            }

            var clampedGraceSeconds = Math.Clamp(graceSeconds, 0, 2);
            return (now - lastPhysicalInputAt).TotalSeconds < clampedGraceSeconds;
        }

        private static bool IsAnyPhysicalInputDown()
        {
            for (var virtualKey = FirstVirtualKey; virtualKey <= LastVirtualKey; virtualKey++)
            {
                if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
