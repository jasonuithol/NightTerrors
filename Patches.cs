using HarmonyLib;
using UnityEngine;

namespace NightTerrors
{
    // ── Sleep trigger ────────────────────────────────────────────────────────

    // Fires server-side when all players have successfully slept.
    // Confirmed via decompile: called from Game.UpdateSleeping() on the server.
    [HarmonyPatch(typeof(EnvMan), "SkipToMorning")]
    static class Patch_SkipToMorning
    {
        static void Prefix()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (NightTerrorsEvent.IsActive) return;

            int chance = NightTerrorsPlugin.CfgTriggerChance.Value;
            if (chance <= 0 || UnityEngine.Random.Range(0, chance) != 0) return;

            NightTerrorsEvent.Begin();
        }
    }

    // ── Death handling ───────────────────────────────────────────────────────

    // Fires on the owning client when the local player dies.
    [HarmonyPatch(typeof(Player), "OnDeath")]
    static class Patch_Player_OnDeath
    {
        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!NightTerrorsClient.IsEventActive) return;

            NightTerrorsPlugin.Log.LogInfo("NightTerrors: local player died during event.");
            // Notify server. InvokeRoutedRPC(string) sends to server automatically.
            ZRoutedRpc.instance.InvokeRoutedRPC("NightTerrors_PlayerDied");
        }
    }

    // ── Skill loss suppression ───────────────────────────────────────────────

    // Confirmed via decompile: Skills.OnDeath() takes no parameters.
    // It is called from Player.OnDeath() only on HardDeath (not soft death).
    [HarmonyPatch(typeof(Skills), "OnDeath")]
    static class Patch_Skills_OnDeath
    {
        static bool Prefix()
        {
            if (NightTerrorsClient.IsEventActive)
            {
                NightTerrorsPlugin.Log.LogInfo("NightTerrors: skill loss suppressed.");
                return false; // skip original
            }
            return true;
        }
    }

    // ── Tombstone suppression ────────────────────────────────────────────────

    // When a player dies during the event, we don't want a tombstone created:
    //  - It would duplicate items (for KeepGear), or leave junk in the wilderness.
    //  - Inventory is saved/restored by the mod; tombstone is redundant.
    // Confirmed via decompile: CreateTombStone() calls MoveInventoryToGrave() which
    // drains the player's inventory into the tombstone container.
    [HarmonyPatch(typeof(Player), "CreateTombStone")]
    static class Patch_CreateTombStone
    {
        static bool Prefix()
        {
            if (NightTerrorsClient.IsEventActive)
            {
                NightTerrorsPlugin.Log.LogInfo("NightTerrors: tombstone suppressed.");
                return false; // skip tombstone creation
            }
            return true;
        }
    }

    // ── Sleep message suppression ────────────────────────────────────────────

    // Suppress "Day X" announcement and morning music during the event.
    // SuppressMorning stays true from event start until the player respawns at their bed,
    // at which point we show the message ourselves (see Patch_Player_OnSpawned).
    // OnMorning() is private — patch by name.
    [HarmonyPatch(typeof(EnvMan), "OnMorning")]
    static class Patch_EnvMan_OnMorning
    {
        static bool Prefix() => !NightTerrorsClient.SuppressMorning;
    }

    // When the player respawns after the event, deliver the suppressed "Day X" message.
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    static class Patch_Player_OnSpawned
    {
        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!NightTerrorsClient.SuppressMorning) return;

            NightTerrorsClient.SuppressMorning = false;

            if (EnvMan.instance != null)
                __instance.Message(MessageHud.MessageType.Center,
                    $"Day {EnvMan.instance.GetDay()}");
        }
    }

    // Suppress "Good morning" on wake-up and dream text during event.
    // Player.Message passes the raw localization key ("$msg_goodmorning") — match on that.
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    static class Patch_Player_Message
    {
        static bool Prefix(string msg)
        {
            if (!(NightTerrorsClient.IsEventStarting || NightTerrorsClient.IsEventActive))
                return true;
            if (msg == "$msg_goodmorning")
                return false; // suppress
            return true;
        }
    }

    // SleepText.ShowDreamText() is private — patch by name.
    // Fires 4 seconds after sleep starts; by then SaveInventory has arrived and
    // IsEventStarting is true, so we can suppress cleanly.
    [HarmonyPatch(typeof(SleepText), "ShowDreamText")]
    static class Patch_SleepText_ShowDreamText
    {
        static bool Prefix()
        {
            if (NightTerrorsClient.IsEventStarting || NightTerrorsClient.IsEventActive)
                return false; // suppress dream text
            return true;
        }
    }

    // ── Item / biome discovery suppression ──────────────────────────────────

    // Suppress "new item!" popups and the permanent m_knownMaterial update during
    // the event. Players should discover items for real in actual gameplay.
    [HarmonyPatch(typeof(Player), nameof(Player.AddKnownItem))]
    static class Patch_AddKnownItem
    {
        static bool Prefix()
        {
            if (NightTerrorsClient.IsEventActive || NightTerrorsClient.IsEventStarting)
                return false; // skip
            return true;
        }
    }

    // AddKnownBiome is private — patch by name.
    // Suppress biome discovery popup and m_knownBiome update during the event.
    [HarmonyPatch(typeof(Player), "AddKnownBiome")]
    static class Patch_AddKnownBiome
    {
        static bool Prefix()
        {
            if (NightTerrorsClient.IsEventActive || NightTerrorsClient.IsEventStarting)
                return false; // skip
            return true;
        }
    }

    // ── Friendly fire ────────────────────────────────────────────────────────

    // The game blocks player-to-player damage via this check in Character.Damage():
    //   (IsPlayer() && !IsPVPEnabled() && attacker.IsPlayer() && !hit.m_ignorePVP)
    // Setting hit.m_ignorePVP = true bypasses it cleanly.
    // HitData is a class (reference type) so mutating in Prefix affects the original.
    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    static class Patch_FriendlyFire
    {
        static void Prefix(HitData hit, Character __instance)
        {
            if (!NightTerrorsClient.IsEventActive) return;
            if (!NightTerrorsPlugin.CfgFriendlyFire.Value) return;
            if (!__instance.IsPlayer()) return;

            Character attacker = hit.GetAttacker();
            if (attacker == null || !attacker.IsPlayer()) return;

            hit.m_ignorePVP = true;
        }
    }
}
