using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BusGut
{
    /// Registers with RevaMP's (TLD Revamp) multiplayer API, so RevaMP refuses a join when host and joiner don't both
    /// have the same BusGut: a player without it would have seats and handrails where the others have open floor.
    /// Without RevaMP nothing happens here (single player, or the game's own multiplayer).
    internal static class RevampLink
    {
        public static bool Registered;

        public static void Init()
        {
            if (AccessTools.TypeByName("TLDRevamp.Multiplayer.MpApi") == null) return;   // no RevaMP
            RegisterWithRevamp();
        }

        // In its own method: the MpApi types only load when RevaMP is there.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWithRevamp()
        {
            try
            {
                TLDRevamp.Multiplayer.MpApi.RegisterMod("busgut", "BusGut", Plugin.Version, typeof(RevampLink).Assembly);
                Registered = true;
            }
            catch (Exception e) { Plugin.Warn("RevaMP mod registration: " + e.Message); }
        }
    }
}
