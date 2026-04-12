using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LegionCEPatch
{
    internal static class CombatExtendedRecoilStateResetter
    {
        private static readonly Type DrawEquipmentPatchType =
            AccessTools.TypeByName("CombatExtended.HarmonyCE.Harmony_PawnRenderer_DrawEquipmentAiming");

        private static readonly FieldInfo EquipmentField = AccessTools.Field(DrawEquipmentPatchType, "equipment");
        private static readonly FieldInfo RecoilOffsetField = AccessTools.Field(DrawEquipmentPatchType, "recoilOffset");
        private static readonly FieldInfo MuzzleJumpField = AccessTools.Field(DrawEquipmentPatchType, "muzzleJump");
        private static readonly FieldInfo CasingDrawPosField = AccessTools.Field(DrawEquipmentPatchType, "casingDrawPos");

        public static bool IsAvailable => DrawEquipmentPatchType != null;

        public static void Reset()
        {
            if (DrawEquipmentPatchType == null)
            {
                return;
            }

            EquipmentField?.SetValue(null, null);
            RecoilOffsetField?.SetValue(null, Vector3.zero);
            MuzzleJumpField?.SetValue(null, 0f);
            CasingDrawPosField?.SetValue(null, Vector3.zero);
        }
    }
}
