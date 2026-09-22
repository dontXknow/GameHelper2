// <copyright file="DynamicConditionState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.DynamicConditions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States;
    using ClickableTransparentOverlay.Win32;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions.Interface;
    using System.Linq.Dynamic.Core.CustomTypeProviders;
    using GameOffsets.Objects.Components;

    /// <summary>
    ///     The structure that can be queried using DynamicCondition
    /// </summary>
    [DynamicLinqType]
    public class DynamicConditionState : IDynamicConditionState
    {
        private const float MovementDistanceThresholdSquared = 0.25f;
        private const double MovementHoldMilliseconds = 120;
        private static readonly object MovementStateLock = new();
        private static Vector2 previousPlayerPosition;
        private static DateTime lastMovementAt = DateTime.MinValue;
        private static uint movementPlayerId;
        private static bool hasMovementSample;
        private static readonly object BarrageStateLock = new();
        private static uint barragePlayerId;
        private static bool hasBarrageSample;
        private static bool previousBarrageEmpowered;
        private static bool previousBarrageUsable;
        private static int previousFrenzyCharges;
        private static int activeBarrageFrenzyCharges;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private readonly Lazy<NearbyMonsterInfo> nearbyMonsterInfo = null!;

        /// <summary>
        ///     Creates a new instance
        /// </summary>
        /// <param name="state">State to build the structure from</param>
        public DynamicConditionState(InGameState state)
        {
            if (state != null)
            {
                var player = state.CurrentAreaInstance.Player;
                Buffs? playerBuffsComponent = null;
                Actor? actorComponent = null;
                Stats? statsComponent = null;
                if (player.TryGetComponent<Buffs>(out var playerBuffs))
                {
                    playerBuffsComponent = playerBuffs;
                    this.PlayerAilments = JsonDataHelper.StatusEffectGroups
                                                  .Where(x => x.Value.Any(playerBuffs.StatusEffects.ContainsKey))
                                                  .Select(x => x.Key).ToHashSet();
                    this.PlayerBuffs = new BuffDictionary(playerBuffs.StatusEffects);
                }

                if (player.TryGetComponent<Actor>(out var actor))
                {
                    actorComponent = actor;
                    this.PlayerAnimation = (int)actor.Animation;
                    this.PlayerIsMoving = DetectPlayerMovement(
                        player,
                        actor,
                        out var stationarySeconds);
                    this.PlayerStationarySeconds = stationarySeconds;
                    this.PlayerSkillIsUseable = actor.IsSkillUsable;
                    this.MinionCommandSkillIsUsable = actor.UsableMinionCommandSkills;
                    this.DeployedObjectsCount = actor.DeployedEntities;
                    this.ActiveSkills = actor.ActiveSkills;
                }

                if (player.TryGetComponent<Life>(out var lifeComponent))
                {
                    this.PlayerVitals = new VitalsInfo(lifeComponent);
                }

                if (player.TryGetComponent<Stats>(out var statsComp))
                {
                    statsComponent = statsComp;
                    this.PlayerFirstWeaponSetActive = statsComp.CurrentWeaponIndex == 0;
                    this.PlayerSecondWeaponSetActive = statsComp.CurrentWeaponIndex == 1;
                    this.PlayerIsShapeShifted = statsComp.IsInShapeshiftedForm;
                }
                else
                {
                    this.PlayerFirstWeaponSetActive = false;
                    this.PlayerSecondWeaponSetActive = false;
                    this.PlayerIsShapeShifted = false;
                }

                UpdateBarrageState(
                    player.Id,
                    playerBuffsComponent,
                    actorComponent,
                    statsComponent,
                    out var barrageIsEmpowered,
                    out var barrageIsUsable,
                    out var currentFrenzyCharges,
                    out var empoweredFrenzyCharges);
                this.BarrageIsEmpowered = barrageIsEmpowered;
                this.BarrageIsUsable = barrageIsUsable;
                this.BarrageCurrentFrenzyCharges = currentFrenzyCharges;
                this.BarrageEmpoweredFrenzyCharges = empoweredFrenzyCharges;
                this.BarrageUpgradeAvailable = barrageIsEmpowered &&
                                               currentFrenzyCharges > empoweredFrenzyCharges;

                this.Flasks = new FlasksInfo(state);
                this.nearbyMonsterInfo = new Lazy<NearbyMonsterInfo>(() => new NearbyMonsterInfo(state));
            }
        }

        /// <summary>
        ///     The buff list
        /// </summary>
        public IBuffDictionary PlayerBuffs { get; } = null!;

        /// <summary>
        ///     The current animation
        /// </summary>
        public int PlayerAnimation { get; }

        /// <summary>
        ///     Gets a value indicating whether the player is currently moving.
        /// </summary>
        public bool PlayerIsMoving { get; }

        /// <summary>
        ///     Gets the time in seconds since player movement was last detected.
        /// </summary>
        public double PlayerStationarySeconds { get; }

        /// <summary>
        ///     Gets a value indicating whether Barrage currently empowers the next attack.
        /// </summary>
        public bool BarrageIsEmpowered { get; }

        /// <summary>
        ///     Gets a value indicating whether Barrage is currently off cooldown.
        /// </summary>
        public bool BarrageIsUsable { get; }

        /// <summary>
        ///     Gets the player's current number of Frenzy Charges.
        /// </summary>
        public int BarrageCurrentFrenzyCharges { get; }

        /// <summary>
        ///     Gets the number of Frenzy Charges used to create the active Barrage empowerment.
        /// </summary>
        public int BarrageEmpoweredFrenzyCharges { get; }

        /// <summary>
        ///     Gets a value indicating whether the current Frenzy Charges would create a stronger
        ///     Barrage empowerment than the active one.
        /// </summary>
        public bool BarrageUpgradeAvailable { get; }

        /// <summary>
        ///     The player skill useability status.
        /// </summary>
        public HashSet<string> PlayerSkillIsUseable { get; } = new();

        /// <summary>
        ///     The names of minion "command" skills usable on at least one summoned minion.
        /// </summary>
        public HashSet<string> MinionCommandSkillIsUsable { get; } = new();

        /// <summary>
        ///   The player skill details are in this structure.
        /// </summary>
        public Dictionary<string, ActiveSkillDetails> ActiveSkills { get; } = new();

        /// <summary>
        ///     The objects deployed by the player, indexed by object-type id (absent ids read as 0).
        /// </summary>
        public DeployedObjectCounter DeployedObjectsCount { get; } = new();

        /// <summary>
        ///     The ailment list
        /// </summary>
        public IReadOnlyCollection<string> PlayerAilments { get; } = new List<string>();

        /// <summary>
        ///     The vitals information
        /// </summary>
        public IVitalsInfo PlayerVitals { get; } = null!;

        /// <summary>
        ///     The flask information
        /// </summary>
        public IFlasksInfo Flasks { get; } = null!;

        /// <summary>
        ///     Calculates the number of nearby monsters given a rarity selector in the outer circle.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <returns></returns>
        public int MonsterCount(MonsterRarity rarity) =>
            this.nearbyMonsterInfo.Value.GetMonsterCount(rarity, MonsterNearbyZones.OuterCircle);

        /// <summary>
        ///     Calculates the number of nearby monsters given a rarity selector
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="zone">circle in which the monster exists</param>
        /// <returns></returns>
        public int MonsterCount(MonsterRarity rarity, MonsterNearbyZones zone) =>
            this.nearbyMonsterInfo.Value.GetMonsterCount(rarity, zone);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently undamageable
        ///     (in an invulnerability phase) in the outer circle.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <returns></returns>
        public int UndamageableMonsterCount(MonsterRarity rarity) =>
            this.nearbyMonsterInfo.Value.GetUndamageableMonsterCount(rarity, MonsterNearbyZones.OuterCircle);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently undamageable
        ///     (in an invulnerability phase).
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="zone">circle in which the monster exists</param>
        /// <returns></returns>
        public int UndamageableMonsterCount(MonsterRarity rarity, MonsterNearbyZones zone) =>
            this.nearbyMonsterInfo.Value.GetUndamageableMonsterCount(rarity, zone);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently damageable
        ///     (i.e. NOT in an invulnerability phase) in the outer circle.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <returns></returns>
        public int DamageableMonsterCount(MonsterRarity rarity) =>
            this.MonsterCount(rarity) - this.UndamageableMonsterCount(rarity);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently damageable
        ///     (i.e. NOT in an invulnerability phase).
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="zone">circle in which the monster exists</param>
        /// <returns></returns>
        public int DamageableMonsterCount(MonsterRarity rarity, MonsterNearbyZones zone) =>
            this.MonsterCount(rarity, zone) - this.UndamageableMonsterCount(rarity, zone);

        /// <summary>
        ///     Counts nearby monsters of the given rarity within an explicit distance, ignoring the
        ///     configured inner/outer circle. Distance is in the same units as those circles and can
        ///     reach the network bubble (~150); monsters beyond that are not loaded by the game.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="maxDistance">Maximum distance from the player</param>
        /// <returns></returns>
        public int MonsterCountInRange(MonsterRarity rarity, int maxDistance) =>
            this.nearbyMonsterInfo.Value.GetMonsterCountInRange(rarity, maxDistance);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently undamageable
        ///     (in an invulnerability phase) within an explicit distance.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="maxDistance">Maximum distance from the player</param>
        /// <returns></returns>
        public int UndamageableMonsterCountInRange(MonsterRarity rarity, int maxDistance) =>
            this.nearbyMonsterInfo.Value.GetUndamageableMonsterCountInRange(rarity, maxDistance);

        /// <summary>
        ///     Counts nearby monsters of the given rarity that are currently damageable
        ///     (i.e. NOT in an invulnerability phase) within an explicit distance.
        /// </summary>
        /// <param name="rarity">The rarity selector for monster search</param>
        /// <param name="maxDistance">Maximum distance from the player</param>
        /// <returns></returns>
        public int DamageableMonsterCountInRange(MonsterRarity rarity, int maxDistance) =>
            this.MonsterCountInRange(rarity, maxDistance) - this.UndamageableMonsterCountInRange(rarity, maxDistance);

        /// <summary>
        ///     Counts nearby corpses (dead monsters) of the given rarity in the outer circle.
        /// </summary>
        /// <param name="rarity">The rarity selector for corpse search</param>
        /// <returns></returns>
        public int CorpseCount(MonsterRarity rarity) =>
            this.nearbyMonsterInfo.Value.GetCorpseCount(rarity, MonsterNearbyZones.OuterCircle);

        /// <summary>
        ///     Counts nearby corpses (dead monsters) of the given rarity.
        /// </summary>
        /// <param name="rarity">The rarity selector for corpse search</param>
        /// <param name="zone">circle in which the corpse exists</param>
        /// <returns></returns>
        public int CorpseCount(MonsterRarity rarity, MonsterNearbyZones zone) =>
            this.nearbyMonsterInfo.Value.GetCorpseCount(rarity, zone);

        /// <summary>
        ///     Counts nearby corpses (dead monsters) of the given rarity within an explicit distance.
        /// </summary>
        /// <param name="rarity">The rarity selector for corpse search</param>
        /// <param name="maxDistance">Maximum distance from the player</param>
        /// <returns></returns>
        public int CorpseCountInRange(MonsterRarity rarity, int maxDistance) =>
            this.nearbyMonsterInfo.Value.GetCorpseCountInRange(rarity, maxDistance);

        /// <summary>
        ///     Number of friendly nearby monsters in the inner circle.
        /// </summary>
        public int InnerCircleFriendlyMonsterCount =>
            this.nearbyMonsterInfo.Value.FriendlyCount[0];

        /// <summary>
        ///     Number of friendly nearby monsters in the outer circle.
        /// </summary>
        public int OuterCircleFriendlyMonsterCount =>
            this.nearbyMonsterInfo.Value.FriendlyCount[1];

        /// <summary>
        ///     For backword compatibility reasons: <inheritdoc cref="OuterCircleFriendlyMonsterCount"/>
        /// </summary>
        public int FriendlyMonsterCount => this.OuterCircleFriendlyMonsterCount;

        /// <summary>
        ///     Gets the value indicating if first weapon set is active or not.
        /// </summary>
        public bool PlayerFirstWeaponSetActive { get; }

        /// <summary>
        ///     Gets the value indicating if second weapon set is active or not.
        /// </summary>
        public bool PlayerSecondWeaponSetActive { get; }

        /// <summary>
        ///     Gets the value indicating if player is shapeshifted or not.
        /// </summary>
        public bool PlayerIsShapeShifted { get; }

        /// <summary>
        ///     Capture the key press event
        /// </summary>
        public bool IsKeyPressedForAction(VK vk) => Utils.IsKeyPressed(vk);

        /// <summary>
        ///     Detect whether a keyboard or mouse key is currently held down.
        /// </summary>
        public bool IsKeyDownForAction(VK vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

        /// <summary>
        ///     Determines whether Barrage should be used. An active empowerment is replaced only
        ///     when the currently available Frenzy Charges would make it stronger.
        /// </summary>
        /// <param name="minimumFrenzyCharges">Minimum current Frenzy Charges required.</param>
        /// <returns>True when Barrage is ready and worth using.</returns>
        public bool ShouldUseBarrage(int minimumFrenzyCharges) =>
            this.BarrageIsUsable &&
            this.BarrageCurrentFrenzyCharges >= minimumFrenzyCharges &&
            (!this.BarrageIsEmpowered || this.BarrageUpgradeAvailable);

        private static void UpdateBarrageState(
            uint playerId,
            Buffs? buffs,
            Actor? actor,
            Stats? stats,
            out bool isEmpowered,
            out bool isUsable,
            out int currentCharges,
            out int empoweredCharges)
        {
            isEmpowered = buffs?.StatusEffects.Keys.Any(IsBarrageBuffName) == true;
            currentCharges = buffs?.StatusEffects
                .Where(x => IsFrenzyChargeBuffName(x.Key))
                .Select(x => Math.Max(0, (int)x.Value.Charges))
                .DefaultIfEmpty(0)
                .Max() ?? 0;

            var hasBarrageSkill = actor?.ActiveSkills.Keys.Any(IsBarrageSkillName) == true;
            isUsable = hasBarrageSkill && actor!.IsSkillUsable.Any(IsBarrageSkillName);

            var reportedConsumedCharges = 0;
            if (stats != null)
            {
                stats.StatsChangedByBuffAndActions.TryGetValue(
                    GameStats.effective_removed_frenzy_charges_count,
                    out reportedConsumedCharges);
                reportedConsumedCharges = Math.Max(0, reportedConsumedCharges);
            }

            lock (BarrageStateLock)
            {
                if (!hasBarrageSample || barragePlayerId != playerId)
                {
                    barragePlayerId = playerId;
                    hasBarrageSample = true;
                    previousBarrageEmpowered = isEmpowered;
                    previousBarrageUsable = isUsable;
                    previousFrenzyCharges = currentCharges;
                    activeBarrageFrenzyCharges = isEmpowered
                        ? Math.Max(currentCharges, reportedConsumedCharges)
                        : 0;
                    empoweredCharges = activeBarrageFrenzyCharges;
                    return;
                }

                // Barrage always starts a cooldown when used. This transition also catches a
                // recast that replaces an existing Barrage buff without the buff disappearing.
                var barrageWasJustUsed = previousBarrageUsable && !isUsable;
                var empowermentJustAppeared = !previousBarrageEmpowered && isEmpowered;
                if (barrageWasJustUsed || empowermentJustAppeared)
                {
                    activeBarrageFrenzyCharges = Math.Max(
                        previousFrenzyCharges,
                        reportedConsumedCharges);
                }
                else if (!isEmpowered)
                {
                    activeBarrageFrenzyCharges = 0;
                }
                else if (reportedConsumedCharges > activeBarrageFrenzyCharges)
                {
                    // Heightened Charges and similar effects can make the effective consumed
                    // amount exceed the visible charge count. Prefer the game's reported value.
                    activeBarrageFrenzyCharges = reportedConsumedCharges;
                }

                previousBarrageEmpowered = isEmpowered;
                previousBarrageUsable = isUsable;
                previousFrenzyCharges = currentCharges;
                empoweredCharges = activeBarrageFrenzyCharges;
            }
        }

        private static bool IsBarrageBuffName(string name) =>
            name.Equals("empower_barrage_visual", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("empower_barrage_visual_", StringComparison.OrdinalIgnoreCase);

        private static bool IsFrenzyChargeBuffName(string name) =>
            name.Equals("frenzy_charge", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("frenzy_charge_", StringComparison.OrdinalIgnoreCase);

        private static bool IsBarrageSkillName(string name) =>
            name.Equals("Barrage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("barrage", StringComparison.OrdinalIgnoreCase);

        private static bool DetectPlayerMovement(
            GameHelper.RemoteObjects.States.InGameStateObjects.Entity player,
            Actor actor,
            out double stationarySeconds)
        {
            var animationName = actor.Animation.ToString();
            var animationIndicatesMovement = actor.Animation == Animation.Run ||
                                             animationName.Contains("Moving", StringComparison.Ordinal);

            if (!player.TryGetComponent<Render>(out var render))
            {
                stationarySeconds = 0;
                return animationIndicatesMovement;
            }

            var worldPosition = render.WorldPosition;
            var currentPosition = new Vector2(worldPosition.X, worldPosition.Y);
            var now = DateTime.UtcNow;
            lock (MovementStateLock)
            {
                if (!hasMovementSample || movementPlayerId != player.Id)
                {
                    movementPlayerId = player.Id;
                    previousPlayerPosition = currentPosition;
                    lastMovementAt = now;
                    hasMovementSample = true;
                    stationarySeconds = 0;
                    return animationIndicatesMovement;
                }

                if (animationIndicatesMovement ||
                    Vector2.DistanceSquared(previousPlayerPosition, currentPosition) >=
                    MovementDistanceThresholdSquared)
                {
                    lastMovementAt = now;
                }

                previousPlayerPosition = currentPosition;
                var isMoving = animationIndicatesMovement ||
                               (now - lastMovementAt).TotalMilliseconds <= MovementHoldMilliseconds;
                stationarySeconds = isMoving ? 0 : (now - lastMovementAt).TotalSeconds;
                return isMoving;
            }
        }
    }
}
