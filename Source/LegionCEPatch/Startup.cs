using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        private const string DeflectionNullVerbWarning =
            "[CE] Deflection for Instigator:{0} Target:{1} DamageDef:{2} Weapon:{3} has null verb, overriding AP.";
        private static readonly Harmony Harmony = new Harmony("aewv.legioncepatch");
        private static readonly MethodInfo ResolveFallbackPenMethod =
            AccessTools.DeclaredMethod(typeof(LegionDeflectionFallbackController), nameof(LegionDeflectionFallbackController.ResolveFallbackPen));

        static Startup()
        {
            LegionWeaponEffectsController.Initialize();

            var patchedMethods = new HashSet<MethodBase>();
            PatchMethod(AccessTools.DeclaredMethod("Verse.Verb_Shoot:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("Verse.Verb_LaunchProjectile:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_ShootCE:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_LaunchProjectileCE:TryCastShot"), patchedMethods);
            PatchMethod(AccessTools.DeclaredMethod("CombatExtended.Verb_ShootMortarCE:TryCastShot"), patchedMethods);
            PatchBeamLaunch();
            PatchDrawEquipmentAiming();
            PatchJobGiverReloadBypass();
            PatchDeflectDamageFallback();
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

            LegionWeaponEffectsController.TryTrigger(__instance);
        }

        private static void PatchBeamLaunch()
        {
            var method = AccessTools.DeclaredMethod(typeof(Beam), nameof(Beam.Launch));
            if (method == null || method.GetMethodBody() == null)
            {
                return;
            }

            Harmony.Patch(method, postfix: new HarmonyMethod(typeof(Startup), nameof(BeamLaunchPostfix)));
        }

        public static void BeamLaunchPostfix(Beam __instance, Thing launcher, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget, Thing equipment)
        {
            LegionWeaponEffectsController.TryTriggerPlasmaImpact(__instance, equipment, launcher, usedTarget, intendedTarget);
        }

        private static void PatchDeflectDamageFallback()
        {
            var method = AccessTools.Method(
                "CombatExtended.ArmorUtilityCE:GetDeflectDamageInfo",
                new[]
                {
                    typeof(DamageInfo),
                    typeof(BodyPartRecord),
                    typeof(float).MakeByRefType(),
                    typeof(float).MakeByRefType(),
                    typeof(bool)
                });

            if (method == null)
            {
                return;
            }

            Harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Startup), nameof(DeflectDamageInfoTranspiler)));
        }

        public static IEnumerable<CodeInstruction> DeflectDamageInfoTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            if (ResolveFallbackPenMethod == null)
            {
                return instructions;
            }

            var codes = instructions.ToList();
            var startIndex = codes.FindIndex(
                code =>
                    code.opcode == OpCodes.Ldstr &&
                    string.Equals(code.operand as string, DeflectionNullVerbWarning, StringComparison.Ordinal));

            if (startIndex < 0)
            {
                Log.Warning("[Legion CE Patch] Failed to locate CE null-verb deflection warning block.");
                return codes;
            }

            var endIndex = -1;
            for (var index = startIndex; index < codes.Count; index++)
            {
                if (codes[index].opcode == OpCodes.Stloc_1)
                {
                    endIndex = index;
                    break;
                }
            }

            if (endIndex < 0)
            {
                Log.Warning("[Legion CE Patch] Failed to locate CE null-verb deflection fallback store.");
                return codes;
            }

            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, ResolveFallbackPenMethod),
                new CodeInstruction(OpCodes.Stloc_1)
            };

            replacement[0].labels.AddRange(codes[startIndex].labels);
            replacement[0].blocks.AddRange(codes[startIndex].blocks);

            codes.RemoveRange(startIndex, endIndex - startIndex + 1);
            codes.InsertRange(startIndex, replacement);
            return codes;
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
