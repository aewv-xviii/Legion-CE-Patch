using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace LegionCEPatch
{
    public static class LegacyMuzzleFlashController
    {
        private const string SourcePackageId = "Dogdough.Aegiscorp";

        private static readonly Dictionary<string, EffecterDef> EffectersByWeaponDefName =
            new Dictionary<string, EffecterDef>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, float> OriginalMuzzleFlashScaleByWeaponDefName =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<Verb, int> LastProcessedShotTickByVerb =
            new Dictionary<Verb, int>();

        public static void Initialize()
        {
            LongEventHandler.ExecuteWhenFinished(RebuildLookup);
        }

        private static void RebuildLookup()
        {
            EffectersByWeaponDefName.Clear();
            OriginalMuzzleFlashScaleByWeaponDefName.Clear();
            LastProcessedShotTickByVerb.Clear();

            var sourceMod = LoadedModManager.RunningModsListForReading.FirstOrDefault(
                mod =>
                    string.Equals(mod.PackageId, SourcePackageId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mod.PackageIdPlayerFacing, SourcePackageId, StringComparison.OrdinalIgnoreCase));

            if (sourceMod == null)
            {
                Log.Warning("[Legion CE Patch] Could not find Legion source mod while building muzzle flash lookup.");
                return;
            }

            var projectileEffecters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceProjectilesByWeapon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceMuzzleFlashScaleByWeapon = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in sourceMod.LoadDefs(false))
            {
                try
                {
                    var document = XDocument.Load(asset.FullFilePath);
                    var root = document.Root;
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (var thingDef in root.Elements("ThingDef"))
                    {
                        var defName = thingDef.Element("defName")?.Value?.Trim();
                        if (string.IsNullOrEmpty(defName))
                        {
                            continue;
                        }

                        var effecterDefName = TryGetProjectileEffecterDefName(thingDef);
                        if (!string.IsNullOrEmpty(effecterDefName) && !projectileEffecters.ContainsKey(defName))
                        {
                            projectileEffecters[defName] = effecterDefName;
                        }

                        var projectileDefName = TryGetWeaponProjectileDefName(thingDef);
                        if (!string.IsNullOrEmpty(projectileDefName) && !sourceProjectilesByWeapon.ContainsKey(defName))
                        {
                            sourceProjectilesByWeapon[defName] = projectileDefName;
                        }

                        var muzzleFlashScale = TryGetWeaponMuzzleFlashScale(thingDef);
                        if (muzzleFlashScale.HasValue && !sourceMuzzleFlashScaleByWeapon.ContainsKey(defName))
                        {
                            sourceMuzzleFlashScaleByWeapon[defName] = muzzleFlashScale.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[Legion CE Patch] Failed to parse XML asset '{asset.FullFilePath}': {ex.Message}");
                }
            }

            foreach (var pair in sourceProjectilesByWeapon)
            {
                if (!projectileEffecters.TryGetValue(pair.Value, out var effecterDefName))
                {
                    continue;
                }

                var currentWeapon = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Key);
                if (currentWeapon == null)
                {
                    continue;
                }

                var currentProjectileDefName = currentWeapon.Verbs?.FirstOrDefault()?.defaultProjectile?.defName;
                if (string.IsNullOrEmpty(currentProjectileDefName))
                {
                    continue;
                }

                if (string.Equals(currentProjectileDefName, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var effecterDef = DefDatabase<EffecterDef>.GetNamedSilentFail(effecterDefName);
                if (effecterDef != null)
                {
                    EffectersByWeaponDefName[currentWeapon.defName] = effecterDef;
                }

                if (sourceMuzzleFlashScaleByWeapon.TryGetValue(pair.Key, out var originalMuzzleFlashScale))
                {
                    OriginalMuzzleFlashScaleByWeaponDefName[currentWeapon.defName] = originalMuzzleFlashScale;
                }
            }

            Log.Message($"[Legion CE Patch] Registered legacy muzzle flashes for {EffectersByWeaponDefName.Count} CE-converted Legion weapons.");
        }

        public static void TryTrigger(Verb verb)
        {
            if (verb == null)
            {
                return;
            }

            var equipment = verb.EquipmentSource;
            var caster = verb.Caster;
            if (equipment == null || caster == null || caster.MapHeld == null)
            {
                return;
            }

            if (!EffectersByWeaponDefName.TryGetValue(equipment.def.defName, out var effecterDef))
            {
                return;
            }

            var shotTick = verb.LastShotTick >= 0 ? verb.LastShotTick : Find.TickManager.TicksGame;
            if (LastProcessedShotTickByVerb.TryGetValue(verb, out var lastProcessedTick) && lastProcessedTick == shotTick)
            {
                return;
            }

            LastProcessedShotTickByVerb[verb] = shotTick;

            var map = caster.MapHeld;
            var casterCell = caster.PositionHeld;
            var targetInfoA = new TargetInfo(casterCell, map, false);
            var targetInfoB = GetTargetInfo(verb, map, targetInfoA);

            // Legion's muzzle flash effecters already encode their own forward offset and
            // source/target relationship via offsetTowardsTarget + spawnLocType=OnSource.
            var effecter = effecterDef.Spawn(targetInfoA, targetInfoB, 1f);
            effecter.Trigger(targetInfoA, targetInfoB);
            effecter.Cleanup();

            TryNotifyCombatExtendedLighting(verb, equipment.def.defName, casterCell, map);
        }

        private static TargetInfo GetTargetInfo(Verb verb, Map map, TargetInfo fallback)
        {
            var currentTarget = verb.CurrentTarget;
            if (!currentTarget.IsValid)
            {
                return fallback;
            }

            if (currentTarget.HasThing && currentTarget.Thing != null)
            {
                return new TargetInfo(currentTarget.Thing);
            }

            return new TargetInfo(currentTarget.Cell, map, false);
        }

        private static string TryGetWeaponProjectileDefName(XElement thingDef)
        {
            var verbsElement = thingDef.Element("verbs");
            var firstVerb = verbsElement?.Elements("li").FirstOrDefault();
            return firstVerb?.Element("defaultProjectile")?.Value?.Trim();
        }

        private static string TryGetProjectileEffecterDefName(XElement thingDef)
        {
            var compsElement = thingDef.Element("comps");
            if (compsElement == null)
            {
                return null;
            }

            foreach (var comp in compsElement.Elements("li"))
            {
                var classAttribute = comp.Attribute("Class")?.Value;
                if (string.IsNullOrEmpty(classAttribute))
                {
                    continue;
                }

                if (!classAttribute.EndsWith("CompProperties_ProjectileEffecter", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var effecterDef = comp.Element("effecterDef")?.Value?.Trim();
                if (!string.IsNullOrEmpty(effecterDef))
                {
                    return effecterDef;
                }
            }

            return null;
        }

        private static float? TryGetWeaponMuzzleFlashScale(XElement thingDef)
        {
            var verbsElement = thingDef.Element("verbs");
            var firstVerb = verbsElement?.Elements("li").FirstOrDefault();
            var rawValue = firstVerb?.Element("muzzleFlashScale")?.Value?.Trim();
            if (string.IsNullOrEmpty(rawValue))
            {
                return null;
            }

            if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            return value;
        }

        private static void TryNotifyCombatExtendedLighting(Verb verb, string weaponDefName, IntVec3 shooterCell, Map map)
        {
            if (!(verb is Verb_LaunchProjectileCE launchVerb))
            {
                return;
            }

            if (!OriginalMuzzleFlashScaleByWeaponDefName.TryGetValue(weaponDefName, out var originalMuzzleFlashScale))
            {
                return;
            }

            if (originalMuzzleFlashScale <= 0f)
            {
                return;
            }

            var projectileProps = launchVerb.projectilePropsCE;
            var muzzleFlashMultiplier = projectileProps?.muzzleFlashMultiplier ?? 1f;
            var muzzleFlashOffset = projectileProps?.muzzleFlashOffset ?? 0f;
            var intensity = Mathf.Max(0f, originalMuzzleFlashScale * muzzleFlashMultiplier + muzzleFlashOffset);
            if (intensity <= 0f)
            {
                return;
            }

            var lightingTracker = map?.GetComponent<LightingTracker>();
            lightingTracker?.Notify_ShotsFiredAt(shooterCell, intensity);
        }
    }
}
