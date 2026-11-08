// <copyright file="AreaLoadingState.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.States
{
    using System;
    using System.Collections.Generic;
    using Coroutine;
    using CoroutineEvents;
    using GameOffsets.Objects.FilesStructures;
    using GameOffsets.Objects.States;
    using ImGuiNET;

    /// <summary>
    ///     Reads AreaLoadingState Game Object.
    /// </summary>
    public sealed class AreaLoadingState : RemoteObjectBase
    {
        private AreaLoadingStateOffset lastCache;

        /// <summary>
        ///     Initializes a new instance of the <see cref="AreaLoadingState" /> class.
        /// </summary>
        /// <param name="address">address of the remote memory object.</param>
        internal AreaLoadingState(IntPtr address)
            : base(address)
        {
            CoroutineHandler.Start(this.OnPerFrame(), priority: int.MaxValue - 1);
        }

        /// <summary>
        ///     Gets the game current Area Name.
        /// </summary>
        public string CurrentAreaName { get; private set; } = string.Empty;

        /// <summary>
        ///     Gets a value indicating whether the game is in loading screen or not.
        /// </summary>
        internal bool IsLoading { get; private set; }

        /// <summary>
        ///     Converts the <see cref="AreaLoadingState" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Current Area Name: {this.CurrentAreaName}");
            ImGui.Text($"Is Loading Screen: {this.IsLoading}");
            ImGui.Text($"Total Loading Time(ms): {this.lastCache.TotalLoadingScreenTimeMs}");
        }

        /// <inheritdoc />
        protected override void CleanUpData()
        {
            this.lastCache = default;
            this.CurrentAreaName = string.Empty;
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<AreaLoadingStateOffset>(this.Address, out var data))
            {
                return;
            }

            this.IsLoading = data.IsLoading == 0x01;
            if (this.IsLoading ||
                data.TotalLoadingScreenTimeMs <= this.lastCache.TotalLoadingScreenTimeMs ||
                data.CurrentAreaDetailsPtr == IntPtr.Zero ||
                !reader.TryReadMemory<WorldAreaDatOffsets>(data.CurrentAreaDetailsPtr, out var areaDetails) ||
                areaDetails.NamePtr == IntPtr.Zero)
            {
                return;
            }

            var areaName = reader.ReadUnicodeString(areaDetails.NamePtr);
            if (string.IsNullOrEmpty(areaName))
            {
                return;
            }

            this.CurrentAreaName = areaName;
            this.lastCache = data;
            CoroutineHandler.InvokeLater(new Wait(0.1d), () => { CoroutineHandler.RaiseEvent(RemoteEvents.AreaChanged); });
        }

        private IEnumerator<Wait> OnPerFrame()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.PerFrameDataUpdate);
                try
                {
                    if (this.Address != IntPtr.Zero)
                    {
                        this.UpdateData(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AreaLoadingState.OnPerFrame] {ex}");
                }
            }
        }
    }
}
