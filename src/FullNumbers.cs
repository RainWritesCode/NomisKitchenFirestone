using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NomisKitchen
{
    internal static class FullNumbers
    {
        [HarmonyPatch(typeof(Actor), "get_ForceUseBigNumberAbbreviations")]
        internal static class ForcePropertyFalse
        {
            [HarmonyPostfix]
            static void Postfix(ref bool __result)
            {
                if (Plugin.ShowFullNumbers.Value) __result = false;
            }
        }

        [HarmonyPatch]
        internal static class SkipFormatter
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                var formatter = typeof(Actor)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetAttackOrHealthStringAndDoLargeNumberFormatting" && m.ReturnType == typeof(string));
                if (formatter == null)
                {
                    Plugin.Log?.LogWarning("Full numbers: this Hearthstone build has no number formatter to patch.");
                    yield break;
                }
                yield return formatter;
            }

            [HarmonyPrefix]
            static bool Prefix(ref string __result, object[] __args)
            {
                if (!Plugin.ShowFullNumbers.Value || __args == null) return true;
                foreach (var arg in __args)
                {
                    if (arg is int value)
                    {
                        __result = value.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
