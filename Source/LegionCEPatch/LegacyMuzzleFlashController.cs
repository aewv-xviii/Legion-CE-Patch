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
        private const bool DebugLightingLogs = true;

        private sealed class WeaponFlashDiagnostic
        {
            public string WeaponDefName;
            public string SourceProjectileDefName;
            public string CurrentProjectileDefName;
            public string EffecterDefName;
            public float? OriginalMuzzleFlashScale;
            public bool RegisteredLegacyFlash;
        }

        private static readonly Dictionary<string, EffecterDef> EffectersByWeaponDefName =
            new Dictionary<string, EffecterDef>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, float> OriginalMuzzleFlashScaleByWeaponDefName =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, WeaponFlashDiagnostic> DiagnosticsByWeaponDefName =
            new Dictionary<string, WeaponFlashDiagnostic>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> PlasmaBeamWeaponDefNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<Verb, int> LastProcessedShotTickByVerb =
            new Dictionary<Verb, int>();

        private static readonly HashSet<string> LoggedMissingDiagnostics =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static FleckDef PlasmaShotFlashFleckDef;
        private static FleckDef PlasmaAmbientGlowFleckDef;
        private static FleckDef PlasmaAmbientGlowOuterFleckDef;

        public static void Initialize()
        {
            LongEventHandler.ExecuteWhenFinished(RebuildLookup);
        }

        private static void RebuildLookup()
        {
            EffectersByWeaponDefName.Clear();
            OriginalMuzzleFlashScaleByWeaponDefName.Clear();
            DiagnosticsByWeaponDefName.Clear();
            PlasmaBeamWeaponDefNames.Clear();
            LastProcessedShotTickByVerb.Clear();
            LoggedMissingDiagnostics.Clear();
            PlasmaShotFlashFleckDef = DefDatabase<FleckDef>.GetNamedSilentFail("LG_CE_PlasmaShotFlash");
            PlasmaAmbientGlowFleckDef = DefDatabase<FleckDef>.GetNamedSilentFail("LG_CE_PlasmaAmbientGlow");
            PlasmaAmbientGlowOuterFleckDef = DefDatabase<FleckDef>.GetNamedSilentFail("LG_CE_PlasmaAmbientGlowOuter");

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
            var plasmaBeamProjectiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        if (IsPlasmaBeamProjectile(thingDef))
                        {
                            plasmaBeamProjectiles.Add(defName);
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
                var currentWeapon = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Key);
                if (currentWeapon == null)
                {
                    continue;
                }

                var currentProjectileDefName = currentWeapon.Verbs?.FirstOrDefault()?.defaultProjectile?.defName;
                projectileEffecters.TryGetValue(pair.Value, out var effecterDefName);
                sourceMuzzleFlashScaleByWeapon.TryGetValue(pair.Key, out var originalMuzzleFlashScaleValue);

                var diagnostic = new WeaponFlashDiagnostic
                {
                    WeaponDefName = currentWeapon.defName,
                    SourceProjectileDefName = pair.Value,
                    CurrentProjectileDefName = currentProjectileDefName,
                    EffecterDefName = effecterDefName,
                    OriginalMuzzleFlashScale = sourceMuzzleFlashScaleByWeapon.TryGetValue(pair.Key, out var originalMuzzleFlashScale)
                        ? originalMuzzleFlashScale
                        : (float?)null
                };
                DiagnosticsByWeaponDefName[currentWeapon.defName] = diagnostic;

                if (sourceMuzzleFlashScaleByWeapon.TryGetValue(pair.Key, out var originalMuzzleFlashScaleForRegistration))
                {
                    OriginalMuzzleFlashScaleByWeaponDefName[currentWeapon.defName] = originalMuzzleFlashScaleForRegistration;
                }

                if (plasmaBeamProjectiles.Contains(pair.Value))
                {
                    PlasmaBeamWeaponDefNames.Add(currentWeapon.defName);
                }

                if (string.IsNullOrEmpty(effecterDefName) || string.IsNullOrEmpty(currentProjectileDefName))
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
                    diagnostic.RegisteredLegacyFlash = true;
                }
            }

            Log.Message($"[Legion CE Patch] Registered legacy muzzle flashes for {EffectersByWeaponDefName.Count} CE-converted Legion weapons.");
            if (DebugLightingLogs)
            {
                foreach (var diagnostic in DiagnosticsByWeaponDefName.Values.OrderBy(item => item.WeaponDefName))
                {
                    Log.Message(
                        $"[Legion CE Patch] Flash map {diagnostic.WeaponDefName}: " +
                        $"registered={diagnostic.RegisteredLegacyFlash}, " +
                        $"sourceProjectile={diagnostic.SourceProjectileDefName}, " +
                        $"currentProjectile={diagnostic.CurrentProjectileDefName}, " +
                        $"effecter={diagnostic.EffecterDefName}, " +
                        $"originalScale={(diagnostic.OriginalMuzzleFlashScale?.ToString(CultureInfo.InvariantCulture) ?? "null")}.");
                }
            }
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

            var weaponDefName = equipment.def.defName;
            var hasLegacyEffecter = EffectersByWeaponDefName.TryGetValue(weaponDefName, out var effecterDef);
            var hasOriginalMuzzleFlashScale = OriginalMuzzleFlashScaleByWeaponDefName.ContainsKey(weaponDefName);
            if (!hasLegacyEffecter && !hasOriginalMuzzleFlashScale)
            {
                TryLogMissingDiagnostic(weaponDefName, verb);
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

            if (hasLegacyEffecter)
            {
                // Legion's muzzle flash effecters already encode their own forward offset and
                // source/target relationship via offsetTowardsTarget + spawnLocType=OnSource.
                var effecter = effecterDef.Spawn(targetInfoA, targetInfoB, 1f);
                effecter.Trigger(targetInfoA, targetInfoB);
                effecter.Cleanup();
            }

            TryNotifyCombatExtendedLighting(verb, equipment, caster, targetInfoB, map);
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

        private static bool IsPlasmaBeamProjectile(XElement thingDef)
        {
            var thingClass = thingDef.Element("thingClass")?.Value?.Trim();
            var projectileElement = thingDef.Element("projectile");
            if (projectileElement == null)
            {
                return false;
            }

            var beamMoteDef = projectileElement.Element("beamMoteDef")?.Value?.Trim();
            var damageDef = projectileElement.Element("damageDef")?.Value?.Trim();

            var isBeamProjectile =
                (!string.IsNullOrEmpty(thingClass) && thingClass.EndsWith("Beam", StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(beamMoteDef);

            if (!isBeamProjectile)
            {
                return false;
            }

            return string.Equals(damageDef, "Bullet_Plasma", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(beamMoteDef, "Mote_LRBeamLaser", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryLogMissingDiagnostic(string weaponDefName, Verb verb)
        {
            if (!DebugLightingLogs || !LoggedMissingDiagnostics.Add(weaponDefName))
            {
                return;
            }

            if (!DiagnosticsByWeaponDefName.TryGetValue(weaponDefName, out var diagnostic))
            {
                Log.Message(
                    $"[Legion CE Patch] No flash diagnostics for {weaponDefName}. " +
                    $"Verb={verb.GetType().FullName}, currentProjectile={verb.verbProps?.defaultProjectile?.defName ?? "null"}.");
                return;
            }

            Log.Message(
                $"[Legion CE Patch] Missing legacy flash mapping for {weaponDefName}. " +
                $"Verb={verb.GetType().FullName}, " +
                $"sourceProjectile={diagnostic.SourceProjectileDefName}, " +
                $"currentProjectile={diagnostic.CurrentProjectileDefName}, " +
                $"effecter={diagnostic.EffecterDefName}, " +
                $"originalScale={(diagnostic.OriginalMuzzleFlashScale?.ToString(CultureInfo.InvariantCulture) ?? "null")}, " +
                $"registered={diagnostic.RegisteredLegacyFlash}.");
        }

        private static void TryNotifyCombatExtendedLighting(Verb verb, ThingWithComps equipment, Thing caster, TargetInfo targetInfoB, Map map)
        {
            var weaponDefName = equipment.def.defName;
            if (!OriginalMuzzleFlashScaleByWeaponDefName.TryGetValue(weaponDefName, out var originalMuzzleFlashScale))
            {
                if (DebugLightingLogs)
                {
                    Log.Message($"[Legion CE Patch] Skipped CE lighting for {weaponDefName}: no original muzzleFlashScale found.");
                }

                return;
            }

            if (originalMuzzleFlashScale <= 0f)
            {
                if (DebugLightingLogs)
                {
                    Log.Message($"[Legion CE Patch] Skipped CE lighting for {weaponDefName}: original muzzleFlashScale={originalMuzzleFlashScale.ToString(CultureInfo.InvariantCulture)}.");
                }

                return;
            }

            var muzzleFlashMultiplier = 1f;
            var muzzleFlashOffset = 0f;
            if (verb is Verb_LaunchProjectileCE launchVerb)
            {
                var projectileProps = launchVerb.projectilePropsCE;
                muzzleFlashMultiplier = projectileProps?.muzzleFlashMultiplier ?? 1f;
                muzzleFlashOffset = projectileProps?.muzzleFlashOffset ?? 0f;
            }

            var intensity = Mathf.Max(0f, originalMuzzleFlashScale * muzzleFlashMultiplier + muzzleFlashOffset);
            if (intensity <= 0f)
            {
                if (DebugLightingLogs)
                {
                    Log.Message(
                        $"[Legion CE Patch] Skipped CE lighting for {weaponDefName}: computed intensity={intensity.ToString(CultureInfo.InvariantCulture)} " +
                        $"from originalScale={originalMuzzleFlashScale.ToString(CultureInfo.InvariantCulture)}, " +
                        $"multiplier={muzzleFlashMultiplier.ToString(CultureInfo.InvariantCulture)}, " +
                        $"offset={muzzleFlashOffset.ToString(CultureInfo.InvariantCulture)}.");
                }

                return;
            }

            var isPlasmaBeamWeapon = PlasmaBeamWeaponDefNames.Contains(weaponDefName);
            var isSidearmWeapon = IsSidearmWeapon(equipment.def, originalMuzzleFlashScale);
            var visualIntensity = GetVisualFlashIntensity(intensity, isPlasmaBeamWeapon, isSidearmWeapon);
            var lightingIntensity = GetLightingIntensity(intensity, isPlasmaBeamWeapon);
            var visualFlashLocation = GetVisualFlashLocation(caster, equipment, targetInfoB, isSidearmWeapon);
            var lightingTracker = map?.GetComponent<LightingTracker>();
            if (lightingTracker == null)
            {
                if (DebugLightingLogs)
                {
                    Log.Message($"[Legion CE Patch] Skipped CE lighting for {weaponDefName}: LightingTracker missing on map.");
                }

                return;
            }

            var visualFlashFleckDef = GetVisualFlashFleckDef(isPlasmaBeamWeapon);
            if (visualIntensity > 0f && visualFlashFleckDef != null)
            {
                FleckMakerCE.Static(visualFlashLocation, map, visualFlashFleckDef, visualIntensity);
            }

            var ambientGlowFleckDef = GetAmbientGlowFleckDef(isPlasmaBeamWeapon);
            if (ambientGlowFleckDef != null)
            {
                var ambientGlowIntensity = GetAmbientGlowIntensity(visualIntensity);
                if (ambientGlowIntensity > 0f)
                {
                    SpawnAmbientGlow(caster, targetInfoB, map, ambientGlowFleckDef, ambientGlowIntensity);
                }
            }

            if (lightingIntensity > 0f)
            {
                lightingTracker.Notify_ShotsFiredAt(caster.PositionHeld, lightingIntensity);
            }

            if (DebugLightingLogs)
            {
                Log.Message(
                    $"[Legion CE Patch] CE lighting notified for {weaponDefName} at {caster.PositionHeld} with visual flash at {visualFlashLocation}: " +
                    $"verb={verb.GetType().FullName}, " +
                    $"plasmaBeam={isPlasmaBeamWeapon}, " +
                    $"sidearm={isSidearmWeapon}, " +
                    $"originalScale={originalMuzzleFlashScale.ToString(CultureInfo.InvariantCulture)}, " +
                    $"multiplier={muzzleFlashMultiplier.ToString(CultureInfo.InvariantCulture)}, " +
                    $"offset={muzzleFlashOffset.ToString(CultureInfo.InvariantCulture)}, " +
                    $"baseIntensity={intensity.ToString(CultureInfo.InvariantCulture)}, " +
                    $"visualIntensity={visualIntensity.ToString(CultureInfo.InvariantCulture)}, " +
                    $"ambientGlowIntensity={GetAmbientGlowIntensity(visualIntensity).ToString(CultureInfo.InvariantCulture)}, " +
                    $"lightingIntensity={lightingIntensity.ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        private static FleckDef GetVisualFlashFleckDef(bool isPlasmaBeamWeapon)
        {
            if (isPlasmaBeamWeapon)
            {
                return PlasmaShotFlashFleckDef ?? FleckDefOf.ShotFlash;
            }

            return FleckDefOf.ShotFlash;
        }

        private static FleckDef GetAmbientGlowFleckDef(bool isPlasmaBeamWeapon)
        {
            if (!isPlasmaBeamWeapon)
            {
                return null;
            }

            return PlasmaAmbientGlowFleckDef;
        }

        private static float GetVisualFlashIntensity(float baseIntensity, bool isPlasmaBeamWeapon, bool isSidearmWeapon)
        {
            var visualIntensity = baseIntensity;
            if (isPlasmaBeamWeapon)
            {
                visualIntensity *= 0.92f;
            }

            if (isSidearmWeapon)
            {
                visualIntensity = Mathf.Max(visualIntensity * 1.35f, 4.8f);
            }

            return visualIntensity;
        }

        private static float GetAmbientGlowIntensity(float visualIntensity)
        {
            return Mathf.Max(0f, visualIntensity * 0.28f);
        }

        private static float GetLightingIntensity(float baseIntensity, bool isPlasmaBeamWeapon)
        {
            if (isPlasmaBeamWeapon)
            {
                return baseIntensity * 0.0f;
            }

            return baseIntensity;
        }

        private static bool IsSidearmWeapon(ThingDef weaponDef, float originalMuzzleFlashScale)
        {
            if (weaponDef.weaponTags != null)
            {
                foreach (var tag in weaponDef.weaponTags)
                {
                    if (string.Equals(tag, "CE_Sidearm", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, "CE_OneHandedWeapon", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            var drawSize = weaponDef.graphicData?.drawSize.x ?? 0f;
            return originalMuzzleFlashScale <= 4.1f && drawSize <= 0.85f;
        }

        private static Vector3 GetVisualFlashLocation(Thing caster, ThingWithComps equipment, TargetInfo targetInfo, bool isSidearmWeapon)
        {
            var source = caster.DrawPos;
            var direction = GetShotDirection(caster, targetInfo);

            var drawSize = equipment.def.graphicData?.drawSize.x ?? 1f;
            var forwardOffset = isSidearmWeapon
                ? Mathf.Clamp(drawSize * 0.58f, 0.45f, 0.75f)
                : Mathf.Clamp(drawSize * 0.42f, 0.35f, 0.95f);
            return source + direction * forwardOffset;
        }

        private static void SpawnAmbientGlow(Thing caster, TargetInfo targetInfo, Map map, FleckDef ambientGlowFleckDef, float ambientGlowIntensity)
        {
            if (caster == null || map == null || ambientGlowFleckDef == null || ambientGlowIntensity <= 0f)
            {
                return;
            }

            var source = caster.DrawPos;
            var direction = GetShotDirection(caster, targetInfo);
            var lateral = new Vector3(-direction.z, 0f, direction.x);
            if (lateral == Vector3.zero)
            {
                lateral = Vector3.right;
            }

            var outerGlowFleckDef = PlasmaAmbientGlowOuterFleckDef ?? ambientGlowFleckDef;

            FleckMakerCE.Static(source, map, ambientGlowFleckDef, ambientGlowIntensity * 0.7f);
            FleckMakerCE.Static(source + direction * 0.1f, map, ambientGlowFleckDef, ambientGlowIntensity * 0.48f);
            FleckMakerCE.Static(source - direction * 0.02f + lateral * 0.1f, map, outerGlowFleckDef, ambientGlowIntensity * 0.28f);
            FleckMakerCE.Static(source - direction * 0.02f - lateral * 0.1f, map, outerGlowFleckDef, ambientGlowIntensity * 0.28f);
        }

        private static Vector3 GetShotDirection(Thing caster, TargetInfo targetInfo)
        {
            var source = caster.DrawPos;
            var target = targetInfo.IsValid ? targetInfo.CenterVector3 : source + Vector3.forward;
            var direction = (target - source).normalized;
            if (direction == Vector3.zero)
            {
                direction = Vector3.forward;
            }

            return direction;
        }

    }
}
