// The app's own CurseForge API key, the one CurseForge issued to Blessed Hearth.
//
// It's never in the source code or on GitHub. Build-Release.ps1 writes a tiny extra file with
// the key in it while it builds the exe, hands it to the compiler, and deletes it right after.
// That file fills in Load() below. Built from the source without it (or with -NoKey), Load()
// stays empty and the app asks for your own key in Settings like 2.0 did.
//
// Heads up: the key ends up inside the exe, scrambled so it isn't sitting there as plain text,
// but anyone determined enough can still dig it out. That's true of every app that ships with
// a key, and it's how CurseForge expects approved apps to work.

namespace BlessedHearth
{
    public static partial class BuiltInKey
    {
        static partial void Load(ref string key);

        static string loaded;

        public static string Value
        {
            get
            {
                if (loaded == null)
                {
                    string k = null;
                    Load(ref k);
                    loaded = (k ?? "").Trim();
                }
                return loaded;
            }
        }

        public static bool Present => Value != "";
    }
}
