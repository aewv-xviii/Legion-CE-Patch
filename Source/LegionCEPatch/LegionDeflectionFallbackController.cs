using System;
using System.Collections.Generic;
using Verse;

namespace LegionCEPatch
{
    public static class LegionDeflectionFallbackController
    {
        private const string SourcePackageId = "Dogdough.Aegiscorp";
        private const string DeflectionWarningFormat =
            "[CE] Deflection for Instigator:{0} Target:{1} DamageDef:{2} Weapon:{3} has null verb, overriding AP.";

        private static readonly Dictionary<string, float> FallbackSharpPenBySourceDefName =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                // Legion exception weapons keep their source-projectile behavior, but CE's
                // null-verb deflection path still needs CE-scale armor penetration values.
                { "LG_LightFlamer", 0f },
                { "LG_HeavyFlamer", 0f },
                { "LG_HMGfifty", 28f },
                { "LG_Minigunfifty", 28f },
                { "LG_GrenadeMachineGun", 20f },
                { "LG_RecoillessRifle", 70f },
                { "LG_HeavyPlasmaMGA", 45f },
                { "LG_HeavyPlasmaMGB", 45f },
                { "LG_PlasSMG", 35f },
                { "LG_PlasRifle", 50f },
                { "LG_PlasRifleBay", 50f },
                { "LG_PlasRifleGrenadier", 50f },
                { "LG_PlasRifleMedic", 50f },
                { "LG_PlasSniperRifle", 70f },
                { "Gun_50CalRWS", 28f },
                { "Gun_50CalTwinMannable", 28f },
                { "Gun_30mmRWS", 70f },
                { "LGTurret_50CalRWS", 28f },
                { "LGTurret_50CalTwinMannable", 28f },
                { "LGTurret_Thiccturret30mm", 70f },

                // Also cover projectile defs in case CE reports the projectile as the source.
                { "Bullet_FlamyLightFlame", 0f },
                { "Bullet_FlamyHeavyFlame", 0f },
                { "Bullet_LG_HeavyMGFifty", 28f },
                { "Bullet_LG_HeavyMinigun", 28f },
                { "Bullet_LG_40mmAutoGrenadeLauncherProjectile", 20f },
                { "Bullet_LG_84mmHEAT", 70f },
                { "Bullet_PlasMG", 45f },
                { "Bullet_PlasSMG", 35f },
                { "Bullet_PlasRifle", 50f },
                { "Bullet_PlasSniperRifle", 70f },
                { "Bullet_TurretFiftyCal", 28f },
                { "Bullet_TwinTurretFiftyCal", 28f },
                { "Bullet_TwinTurret30mm", 70f }
            };

        public static float ResolveFallbackPen(DamageInfo dinfo)
        {
            if (TryResolveLegionExceptionPen(dinfo.Weapon, out var pen) ||
                TryResolveLegionExceptionPen(dinfo.Instigator?.def, out pen))
            {
                return pen;
            }

            Log.Warning(string.Format(DeflectionWarningFormat, dinfo.Instigator, dinfo.IntendedTarget, dinfo.Def, dinfo.Weapon));
            return 50f;
        }

        private static bool TryResolveLegionExceptionPen(ThingDef thingDef, out float pen)
        {
            pen = 0f;

            if (thingDef == null || string.IsNullOrEmpty(thingDef.defName))
            {
                return false;
            }

            if (!FallbackSharpPenBySourceDefName.TryGetValue(thingDef.defName, out pen))
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
