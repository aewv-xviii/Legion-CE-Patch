using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace LegionCEPatch
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        private const string SourcePackageId = "Dogdough.Aegiscorp";
        private static readonly Harmony Harmony = new Harmony("aewv.legioncepatch");

        static Startup()
        {
            LegacyMuzzleFlashController.Initialize();

            var patchedMethods = new HashSet<MethodBase>();
            PatchMethod(AccessTools.DeclaredMethod("Verse.Verb_Shoot:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("Verse.Verb_LaunchProjectile:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_ShootCE:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_LaunchProjectileCE:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_ShootMortarCE:TryCastShot"), patchedMethods);
            PatchDrawEquipmentAiming();
            PatchJobGiverReloadBypass();
        }

        private static void PatchMethod(MethodInfo method, ISet<MethodBase> patchedMethods)
        {
            if (method == null || method.GetMethodBody() == null || !patchedMethods.Add(method))
            {
                return;
            }

            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(Startup), nameof(TryCastShotPostfix)));
        }

        public static void TryCastShotPostfix(Verb __instance, bool __result)
        {
            if (!__result)
            {
                return;
            }

            LegacyMuzzleFlashController.TryTrigger(__instance);
        }

        private static void PatchDrawEquipmentAiming()
        {
            if (!CombatExtendedRecoilStateResetter.IsAvailable)
            {
                return;
            }

            var method = AccessTools.DeclaredMethod(
                typeof(PawnRenderUtility),
                nameof(PawnRenderUtility.DrawEquipmentAiming),
                new[] { typeof(Thing), typeof(Vector3), typeof(float) });

            if (method == null)
            {
                return;
            }

            var prefix = new HarmonyMethod(typeof(Startup), nameof(DrawEquipmentAimingPrefix))
            {
                priority = Priority.First,
                before = new[] { "CombatExtended.HarmonyCE" }
            };

            var postfix = new HarmonyMethod(typeof(Startup), nameof(DrawEquipmentAimingPostfix))
            {
                priority = Priority.Last,
                after = new[] { "CombatExtended.HarmonyCE" }
            };

            Harmony.Patch(method, prefix: prefix, postfix: postfix);
        }

        public static void DrawEquipmentAimingPrefix()
        {
            CombatExtendedRecoilStateResetter.Reset();
        }

        public static void DrawEquipmentAimingPostfix()
        {
            CombatExtendedRecoilStateResetter.Reset();
        }

        private static void PatchJobGiverReloadBypass()
        {
            var method = AccessTools.Method(typeof(JobGiver_Reload), "TryGiveJob", new[] { typeof(Pawn) });
            if (method == null)
            {
                return;
            }

            var prefix = new HarmonyMethod(typeof(Startup), nameof(JobGiverReloadBypassPrefix))
            {
                priority = Priority.First,
                before = new[] { "CombatExtended.HarmonyCE" }
            };

            Harmony.Patch(method, prefix: prefix);
        }

        public static bool JobGiverReloadBypassPrefix(Pawn pawn, ref Verse.AI.Job __result)
        {
            if (!ShouldBypassLegionReloadJob(pawn))
            {
                return true;
            }

            __result = null;
            return false;
        }

        private static bool ShouldBypassLegionReloadJob(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            var reloadableComp = RimWorld.Utility.ReloadableUtility.FindSomeReloadableComponent(pawn, false);
            if (reloadableComp == null || reloadableComp is CompApparelReloadable)
            {
                return false;
            }

            var reloadThing = (reloadableComp as ThingComp)?.parent ?? GetRelevantReloadThing(pawn);
            if (reloadThing == null)
            {
                return false;
            }

            var modContentPack = reloadThing.def?.modContentPack;
            return
                string.Equals(modContentPack?.PackageId, SourcePackageId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modContentPack?.PackageIdPlayerFacing, SourcePackageId, StringComparison.OrdinalIgnoreCase);
        }

        private static ThingWithComps GetRelevantReloadThing(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }

            var mannedThing = MannableUtility.MannedThing(pawn) as ThingWithComps;
            if (mannedThing != null)
            {
                return mannedThing;
            }

            return pawn.equipment?.Primary;
        }
    }
}
