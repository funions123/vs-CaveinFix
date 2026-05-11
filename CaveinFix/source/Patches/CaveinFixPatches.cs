using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CaveinFix.Patches;

public class CaveinFixPatches : ModSystem
{
    private Harmony _patcher;
    private static bool _patched = false;

    public static ICoreServerAPI _api;
    public static ModConfig Config = new();

    // the instability multiplier actually in effect
    // gotten from the server if on a server, otherwise gotten from the client
    public static float EffectiveMultiplier = 1.0f;

    private const string ChannelName = "caveinfix:configsync";

    public override void Start(ICoreAPI api)
    {
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<ConfigSyncPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        Config = api.LoadModConfig<ModConfig>("caveinfix.json") ?? new ModConfig();
        api.StoreModConfig(Config, "caveinfix.json");
        EffectiveMultiplier = Config.InstabilityMultiplier;

        if (!_patched)
        {
            _patcher = new Harmony(Mod.Info.ModID);
            _patcher.PatchCategory("CaveinFixPatches");
            _patched = true;
        }

        if (!api.ModLoader.IsModEnabled("interestingoregen"))
        {
            api.Logger.Notification("CaveInFix: InterestingOreGen not detected. Enabling IOG cave-in mechanics.");
            _patcher.PatchCategory("InterestingOreGenPatches");
        }
        else
        {
            api.Logger.Notification("CaveInFix: InterestingOreGen detected. Disabling Enabling cave-in mechanics to prevent conflicts.");
        }

        // if a server, send a packet on player join that syncs the client instability config with the server's
        var serverChannel = api.Network.GetChannel(ChannelName) as IServerNetworkChannel;
        api.Event.PlayerJoin += player => serverChannel.SendPacket(new ConfigSyncPacket { InstabilityMultiplier = EffectiveMultiplier }, player);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // load local config only as the initial fallback
        var localConfig = api.LoadModConfig<ModConfig>("caveinfix.json") ?? new ModConfig();
        EffectiveMultiplier = localConfig.InstabilityMultiplier;

        if (!_patched)
        {
            _patcher = new Harmony(Mod.Info.ModID);
            _patcher.PatchCategory("CaveinFixPatches");
            _patched = true;
        }

        (api.Network.GetChannel(ChannelName) as IClientNetworkChannel)
            .SetMessageHandler<ConfigSyncPacket>(packet => EffectiveMultiplier = packet.InstabilityMultiplier);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server)
        {
            return;
        }
    }

    public override void Dispose()
    {
        _patched = false;
        _patcher?.UnpatchAll(Mod.Info.ModID);
        base.Dispose();
    }
}

internal static class Patches
{
    [HarmonyPatchCategory("CaveinFixPatches")]
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock))]
    public static class UnstableRockPatch
    {
        private static readonly System.Reflection.FieldInfo _maxSupportDistanceField =
            AccessTools.Field(typeof(BlockBehaviorUnstableRock), "maxSupportDistance");
        private static readonly System.Reflection.FieldInfo _maxSupportSearchDistanceSqField =
            AccessTools.Field(typeof(BlockBehaviorUnstableRock), "maxSupportSearchDistanceSq");
        private static readonly System.Reflection.MethodInfo _searchCollapsibleMethod =
            AccessTools.Method(typeof(BlockBehaviorUnstableRock), "searchCollapsible");

        // thread-local set of behavior instances that already have scaled fields, preventing double-scaling
        [ThreadStatic]
        private static HashSet<BlockBehaviorUnstableRock> _scaledInstances;

        [HarmonyPatch("searchCollapsible")]
        [HarmonyPostfix]
        public static void SearchCollapsiblePostfix(
            BlockBehaviorUnstableRock __instance,
            BlockPos startPos,
            ICoreAPI ___api,
            ref CollapsibleSearchResult __result
        )
        {
            BlockPos belowPos = startPos.DownCopy();
            Block blockBelow = ___api.World.BlockAccessor.GetBlock(belowPos, BlockLayersAccess.Solid);

            if (blockBelow.BlockId != 0 && blockBelow.SideIsSolid(___api.World.BlockAccessor, belowPos, BlockFacing.UP.Index))
            {
                bool foundVerticalSupport = false;
                double instabilityBelow = 0;

                if (blockBelow.HasBehavior<BlockBehaviorUnstableRock>())
                {
                    instabilityBelow = blockBelow.GetBehavior<BlockBehaviorUnstableRock>().getInstability(belowPos);
                    if (instabilityBelow < 1.0) foundVerticalSupport = true;
                }
                else
                {
                    foundVerticalSupport = true;
                    instabilityBelow = 0;
                }

                if (foundVerticalSupport)
                {
                    __result.Instability = Math.Min(__result.Instability, (float)instabilityBelow);
                    __result.Unconnected = false;
                }
            }
        }

        // getInstability is called recursively by vanilla code so requires some special handling
        [HarmonyPatch("getInstability")]
        [HarmonyPrefix]
        public static bool GetInstabilityPrefix(
            BlockBehaviorUnstableRock __instance,
            BlockPos pos,
            ref double __result
        )
        {
            float multiplier = CaveinFixPatches.EffectiveMultiplier;

            if (multiplier <= 0.0f)
            {
                __result = 0.0;
                return false;
            }

            if (multiplier == 1.0f) return true;

            _scaledInstances ??= new HashSet<BlockBehaviorUnstableRock>();

            if (_scaledInstances.Contains(__instance))
            {
                var res = (CollapsibleSearchResult)_searchCollapsibleMethod.Invoke(__instance, new object[] { pos, false });
                __result = Math.Clamp(res.Instability, 0.0, 1.0);
                return false;
            }

            float origMaxSD = (float)_maxSupportDistanceField.GetValue(__instance);
            float origMaxSDSq = (float)_maxSupportSearchDistanceSqField.GetValue(__instance);

            _maxSupportDistanceField.SetValue(__instance, origMaxSD / multiplier);
            _maxSupportSearchDistanceSqField.SetValue(__instance, origMaxSDSq / (multiplier * multiplier));
            _scaledInstances.Add(__instance);

            try
            {
                var res = (CollapsibleSearchResult)_searchCollapsibleMethod.Invoke(__instance, new object[] { pos, false });
                __result = Math.Clamp(res.Instability, 0.0, 1.0);
            }
            finally
            {
                _maxSupportDistanceField.SetValue(__instance, origMaxSD);
                _maxSupportSearchDistanceSqField.SetValue(__instance, origMaxSDSq);
                _scaledInstances.Remove(__instance);
            }

            return false;
        }
    }

    [HarmonyPatchCategory("InterestingOreGenPatches")]
    [HarmonyPatch(typeof(BlockBehaviorUnstableRock))]
    [HarmonyPatch("OnBlockBroken")]
    public static class CaveInOnBreakPatch
    {
        // --- Primary Epicenter variables ---
        //private static int EpicenterSearchRadius = 10;
        private static int EpicenterPropagationRadiusMin = 2;
        private static int EpicenterPropagationRadiusMax = 4;
        private static double EpicenterThreshold = 1.0;
        private static double CollapseThreshold = -0.01;
        //private static int MaxInitialEpicenters = 1;
        //private static bool PrimaryEpicenterPickMaxOnly = true;

        // (Secondary epicenter system removed — replaced with step-search)

        // Pre-collapse rumble
        //private static string CaveInRumbleSound = "interestingoregen:sounds/cavesounds/caveinrumble.ogg";

        private static Random rng = new Random();

        // ---------------------------
        // New tunable step-search params (can be moved to config)
        // ---------------------------
        private static double MandatoryStepThreshold = 0.75;   // >= this -> forced move (random among candidates)
        private static double BiasStepThreshold = 0.50;        // >= this -> biased move (probabilistic)
        private static double BiasStepChance = 0.70;           // percent to take biased move (0..1)
        private static int MaxSearchSteps = 20;             // how many face-steps to attempt
        private static double DownwardPenalty = 0.6;           // downward move weight multiplier (slight anti-bias)
        private static double EpicenterInstability => EpicenterThreshold; // use existing threshold var

        private static readonly BlockFacing[] Faces = BlockFacing.ALLFACES;

        [HarmonyPrefix]
        public static bool CaveInOnBreakPrefix(BlockBehaviorUnstableRock __instance, IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref EnumHandling handling)
        {
            // Keep original guards
            if (world.Side != EnumAppSide.Server) return true;
            if (byPlayer == null) return true;
            if (!(__instance.AllowFallingBlocks && __instance.CaveIns)) return true;

            // --- Step-based primary epicenter search ---
            BlockPos epicenter = RunEpicenterStepSearch(world, pos);

            if (epicenter == null)
            {
                // no epicenter found — suppress vanilla handling (consistent with prior behavior)
                return false;
            }

            // Found one — trigger rumble and collapse
            TriggerCaveInRumble(world, epicenter);

            int primaryRadius = rng.Next(EpicenterPropagationRadiusMin, EpicenterPropagationRadiusMax + 1);
            CollapseEllipsoidFromEpicenter(world, epicenter, primaryRadius);

            return false; // suppress vanilla
        }

        private static void TriggerCaveInRumble(IWorldAccessor world, BlockPos epicenter)
        {
            // Only play cave-in sound; screen shake removed
            //world.PlaySoundAt(new AssetLocation(CaveInRumbleSound), epicenter.X, epicenter.Y, epicenter.Z, null, true, ShakeRange, 1.5f);
        }

        // ---------------------------
        // Step-search implementation
        // - Walks face-to-face through solid unstable-rock blocks
        // - Never steps into air (except initial block was broken)
        // - Avoids revisiting positions
        // - Mandatory neighbors (>= MandatoryStepThreshold) are chosen randomly amongst themselves
        // - Biased neighbors (>= BiasStepThreshold) chosen with BiasStepChance
        // - Otherwise weighted random (downwards penalized)
        // ---------------------------
        private static BlockPos RunEpicenterStepSearch(IWorldAccessor world, BlockPos start)
        {
            var ba = world.BlockAccessor;
            var visited = new HashSet<BlockPos>(new BlockPosComparer());

            BlockPos current = start.Copy();

            for (int step = 0; step < MaxSearchSteps; step++)
            {
                visited.Add(current);

                // Gather valid neighbors
                var neighbors = new List<(BlockPos pos, double inst, BlockFacing face)>();

                foreach (var face in Faces)
                {
                    BlockPos np = current.AddCopy(face);

                    var nblock = ba.GetBlock(np, BlockLayersAccess.Solid);
                    if (nblock == null || nblock.Id == 0) continue;
                    if (!nblock.HasBehavior<BlockBehaviorUnstableRock>()) continue;
                    if (visited.Contains(np)) continue;

                    var bh = nblock.GetBehavior<BlockBehaviorUnstableRock>();
                    if (bh == null) continue;

                    double inst = bh.getInstability(np);
                    neighbors.Add((np, inst, face));
                }

                if (neighbors.Count == 0) return null; // dead end

                // 1) Mandatory neighbors (>= MandatoryStepThreshold) — pick a random one
                var mandatory = neighbors.Where(n => n.inst >= MandatoryStepThreshold).ToList();
                if (mandatory.Count > 0)
                {
                    var chosen = mandatory[rng.Next(mandatory.Count)];
                    current = chosen.pos.Copy();
                    if (chosen.inst >= EpicenterThreshold)
                    {
                        return FindExposedEpicenter(world, current);
                    }
                    continue;
                }

                // 2) Biased neighbors (>= BiasStepThreshold) — go to one with some probability
                var biased = neighbors.Where(n => n.inst >= BiasStepThreshold).ToList();
                if (biased.Count > 0 && rng.NextDouble() < BiasStepChance)
                {
                    var chosen = biased[rng.Next(biased.Count)];
                    current = chosen.pos.Copy();
                    if (chosen.inst >= EpicenterThreshold)
                    {
                        return FindExposedEpicenter(world, current);
                    }
                    continue;
                }

                // 3) Default: weighted random among all neighbors (apply downward penalty)
                var weighted = new List<(BlockPos pos, double weight, double inst)>();
                foreach (var n in neighbors)
                {
                    double weight = (n.face == BlockFacing.DOWN) ? DownwardPenalty : 1.0;
                    weighted.Add((n.pos, weight, n.inst));
                }

                double totalW = weighted.Sum(w => w.weight);
                double roll = rng.NextDouble() * totalW;
                BlockPos selected = null;
                double selectedInst = 0;

                foreach (var w in weighted)
                {
                    roll -= w.weight;
                    if (roll <= 0)
                    {
                        selected = w.pos;
                        selectedInst = w.inst;
                        break;
                    }
                }

                if (selected == null)
                {
                    var pick = neighbors[rng.Next(neighbors.Count)];
                    current = pick.pos.Copy();
                    if (pick.inst >= EpicenterThreshold)
                    {
                        return FindExposedEpicenter(world, current);
                    }
                }
                else
                {
                    current = selected.Copy();
                    if (selectedInst >= EpicenterThreshold)
                    {
                        return FindExposedEpicenter(world, current);
                    }
                }
            }

            // out of steps
            return null;
        }

        // ---------------------------------------
        // Walk downward until a block is exposed to air
        // Returns null if the support is too strong (instability < EpicenterThreshold)
        // ---------------------------------------
        private static BlockPos FindExposedEpicenter(IWorldAccessor world, BlockPos pos)
        {
            var ba = world.BlockAccessor;
            BlockPos cursor = pos.Copy();

            int safeLimit = 256;

            for (int i = 0; i < safeLimit; i++)
            {
                BlockPos below = cursor.DownCopy();
                var blockBelow = ba.GetBlock(below, BlockLayersAccess.Solid);

                // Air → block above is exposed
                if (blockBelow == null || blockBelow.Id == 0)
                {
                    return cursor.Copy();
                }

                var bh = blockBelow.GetBehavior<BlockBehaviorUnstableRock>();
                if (bh == null) return null; // not an unstable block → treat as stable

                double instability = bh.getInstability(below);
                if (instability < EpicenterThreshold)
                {
                    // Block is stable enough to prevent collapse
                    return null;
                }

                cursor = below;

                if (cursor.Y <= 1) return null; // out of world
            }

            return null; // fail-safe
        }



        private static void CollapseEllipsoidFromEpicenter(IWorldAccessor world, BlockPos center, int baseRadius)
        {
            double a = baseRadius * (0.8 + rng.NextDouble() * 0.6);
            double b = baseRadius * (0.6 + rng.NextDouble() * 0.8);
            double c = baseRadius * (0.8 + rng.NextDouble() * 0.6);

            double a2 = a * a;
            double b2 = b * b;
            double c2 = c * c;

            int radius = (int)Math.Ceiling(Math.Max(a, Math.Max(b, c)));

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        double eq = (dx * dx) / a2 + (dy * dy) / b2 + (dz * dz) / c2;
                        double distortion = (rng.NextDouble() - 0.5) * 0.3;

                        if (eq <= 1 + distortion)
                        {
                            CollapseSingleIfUnstable(world, center.AddCopy(dx, dy, dz));
                        }
                    }
                }
            }
        }

        private static void CollapseSingleIfUnstable(IWorldAccessor world, BlockPos p)
        {
            var block = world.BlockAccessor.GetBlock(p, BlockLayersAccess.Solid);
            if (block == null || !block.HasBehavior<BlockBehaviorUnstableRock>()) return;

            var bh = block.GetBehavior<BlockBehaviorUnstableRock>();
            if (bh == null) return;

            double instability = bh.getInstability(p);
            if (instability <= CollapseThreshold) return;

            Entity existing = world.GetNearestEntity(
                p.ToVec3d().Add(0.5, 0.5, 0.5),
                1, 1.5f,
                (e) => e is EntityBlockFalling ebf && ebf.initialPos.Equals(p)
            );
            if (existing != null) return;

            var collapsedBlock = (Block)AccessTools.Field(typeof(BlockBehaviorUnstableRock), "collapsedBlock").GetValue(bh);
            var fallSound = (AssetLocation)AccessTools.Field(typeof(BlockBehaviorUnstableRock), "fallSound").GetValue(bh);
            var impactDamageMul = (float)AccessTools.Field(typeof(BlockBehaviorUnstableRock), "impactDamageMul").GetValue(bh);
            var dustIntensity = (float)AccessTools.Field(typeof(BlockBehaviorUnstableRock), "dustIntensity").GetValue(bh);

            var fallingBlock = collapsedBlock ?? block;
            var blockEntity = world.BlockAccessor.GetBlockEntity(p);

            if (fallSound == null) fallSound = new AssetLocation("effect/rockslide");

            var entityblock = new EntityBlockFalling(
                fallingBlock,
                blockEntity,
                p,
                fallSound,
                impactDamageMul,
                true,
                dustIntensity
            );

            world.BlockAccessor.SetBlock(0, p);
            world.SpawnEntity(entityblock);
        }

        // ---------------------------
        // BlockPos comparer for HashSet
        // ---------------------------
        private class BlockPosComparer : IEqualityComparer<BlockPos>
        {
            public bool Equals(BlockPos a, BlockPos b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a is null || b is null) return false;
                return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
            }

            public int GetHashCode(BlockPos p)
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + p.X;
                    hash = hash * 31 + p.Y;
                    hash = hash * 31 + p.Z;
                    return hash;
                }
            }
        }
    }

    [HarmonyPatchCategory("InterestingOreGenPatches")]
    [HarmonyPatch(typeof(ModSystemExplosionAffectedStability))]
    public static class DisablePlacedBlockCaveinPatch
    {
        // Patch the private instance method "OnBlockPlacedEvent"
        [HarmonyPrefix]
        [HarmonyPatch("OnBlockPlacedEvent")]
        public static bool OnBlockPlacedEventPrefix(
            IServerPlayer byPlayer,
            int oldblockId,
            BlockSelection blockSel,
            ItemStack withItemStack
        )
        {
            // Returning false prevents the original method executing at all,
            // thereby preventing placement from triggering CheckCollapsible().
            return false;
        }
    }
}
