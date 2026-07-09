using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace WizardMod
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class WizardModPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.hypnodroid.wizardmod";
        public const string NAME = "Wizard Character";
        public const string VERSION = "1.0.0";

        public static BepInEx.Logging.ManualLogSource Log;

        public void Awake()
        {
            Log = Logger;
            Harmony harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(WizardCharacter));
            harmony.PatchAll(typeof(ChaosMagicAbility));
            Log.LogInfo("WizardMod loaded: Wizard character + Chaos Magic ability");
        }

        public static byte[] LoadEmbedded(string fileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(fileName)) continue;
                using (Stream s = asm.GetManifestResourceStream(name))
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            return null;
        }
    }
}
