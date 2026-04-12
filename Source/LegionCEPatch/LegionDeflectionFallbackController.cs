using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace LegionCEPatch
{
    public static class LegionDeflectionFallbackController
    {
        private const string SourcePackageId = "Dogdough.Aegiscorp";
        private const string DeflectionWarningFormat =
            "[CE] Deflection for Instigator:{0} Target:{1} DamageDef:{2} Weapon:{3} has null verb, overriding AP.";

        private static readonly HashSet<string> ExceptionSourceDefNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LG_LightFlamer",
                "LG_HeavyFlamer",
                "LG_HMGfifty",
                "LG_Minigunfifty",
                "LG_GrenadeMachineGun",
                "LG_RecoillessRifle",
                "LG_HeavyPlasmaMGA",
                "LG_HeavyPlasmaMGB",
                "LG_PlasSMG",
                "LG_PlasRifle",
                "LG_PlasRifleBay",
                "LG_PlasRifleGrenadier",
                "LG_PlasRifleMedic",
                "LG_PlasSniperRifle",
                "Gun_50CalRWS",
                "Gun_50CalTwinMannable",
                "Gun_30mmRWS",
                "LGTurret_50CalRWS",
                "LGTurret_50CalTwinMannable",
                "LGTurret_Thiccturret30mm"
            };

        public static float ResolveFallbackPen(DamageInfo dinfo)
        {
            if (IsLegionExceptionSource(dinfo.Weapon) || IsLegionExceptionSource(dinfo.Instigator?.def))
            {
                return Mathf.Max(0f, dinfo.ArmorPenetrationInt);
            }

            Log.Warning(string.Format(DeflectionWarningFormat, dinfo.Instigator, dinfo.IntendedTarget, dinfo.Def, dinfo.Weapon));
            return 50f;
        }

        private static bool IsLegionExceptionSource(ThingDef thingDef)
        {
            if (thingDef == null || string.IsNullOrEmpty(thingDef.defName))
            {
                return false;
            }

            if (!ExceptionSourceDefNames.Contains(thingDef.defName))
            {
                return false;
            }

            var modContentPack = thingDef.modContentPack;
            return
                string.Equals(modContentPack?.PackageId, SourcePackageId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modContentPack?.PackageIdPlayerFacing, SourcePackageId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
