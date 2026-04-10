using System;
using HarmonyLib;
using UnityEngine;

namespace NightTerrors
{
    // ── Client-side state ───────────────────────────────────────────────────

    public static class NightTerrorsClient
    {
        public static bool     IsEventActive;
        public static bool     IsEventStarting;  // set as soon as SaveInventory arrives, before EventStart
        public static bool     SuppressMorning;  // stays true until player respawns after the event
        public static byte[]   SavedInventory;   // original inventory bytes
        public static Scenario CurrentScenario;
    }

    // ── RPC registration + handlers ─────────────────────────────────────────

    static class RpcHandler
    {
        // Called once when ZRoutedRpc initialises (on both server and client).
        // ZRoutedRpc is a plain class (not MonoBehaviour) — hook the constructor.
        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, new[] { typeof(bool) })]
        static class Patch_ZRoutedRpc_Awake
        {
            static void Postfix()
            {
                ZRoutedRpc.instance.Register(
                    "NightTerrors_SaveInventory", new Action<long>(SaveInventory));
                ZRoutedRpc.instance.Register<ZPackage>(
                    "NightTerrors_UploadInventory", UploadInventory);
                ZRoutedRpc.instance.Register<ZPackage>(
                    "NightTerrors_EventStart", EventStart);
                ZRoutedRpc.instance.Register<ZPackage>(
                    "NightTerrors_ReceiveInventory", ReceiveInventory);
                ZRoutedRpc.instance.Register(
                    "NightTerrors_RestoreInventory", new Action<long>(RestoreInventory));
                ZRoutedRpc.instance.Register(
                    "NightTerrors_EventEnd", new Action<long>(EventEnd));
                ZRoutedRpc.instance.Register(
                    "NightTerrors_PlayerDied", new Action<long>(PlayerDied));
                ZRoutedRpc.instance.Register(
                    "NightTerrors_KillSurvivor", new Action<long>(KillSurvivor));
                ZRoutedRpc.instance.Register<string>(
                    "NightTerrors_SetWeather", SetWeather);
            }
        }

        // ── Server → All ────────────────────────────────────────────────────

        // Server tells clients: save your inventory NOW, then upload bytes back.
        static void SaveInventory(long sender)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            NightTerrorsClient.IsEventStarting = true;
            NightTerrorsClient.SuppressMorning  = true;

            var pkg = new ZPackage();
            player.GetInventory().Save(pkg);
            NightTerrorsClient.SavedInventory = pkg.GetArray();
            NightTerrorsPlugin.Log.LogInfo("NightTerrors: inventory saved.");

            // Upload to server for potential swap redistribution.
            var upload = new ZPackage();
            upload.Write(NightTerrorsClient.SavedInventory);
            ZRoutedRpc.instance.InvokeRoutedRPC("NightTerrors_UploadInventory", upload);
        }

        // Server → All: begin event, apply scenario effects locally.
        static void EventStart(long sender, ZPackage pkg)
        {
            int scenarioInt   = pkg.ReadInt();
            bool friendlyFire = pkg.ReadBool();
            int monsterCount  = pkg.ReadInt();
            int kitCount      = pkg.ReadInt();
            string[] kit      = new string[kitCount];
            for (int i = 0; i < kitCount; i++) kit[i] = pkg.ReadString();

            NightTerrorsClient.IsEventActive   = true;
            NightTerrorsClient.CurrentScenario = (Scenario)scenarioInt;

            var player = Player.m_localPlayer;
            if (player == null) return;

            switch (NightTerrorsClient.CurrentScenario)
            {
                case Scenario.GoNaked:
                    player.UnequipAllItems();
                    player.GetInventory().RemoveAll();
                    NightTerrorsPlugin.Log.LogInfo("NightTerrors: stripped for GoNaked.");
                    break;

                case Scenario.DifferentEquipment:
                    player.UnequipAllItems();
                    player.GetInventory().RemoveAll();
                    foreach (string name in kit)
                    {
                        var item = player.GetInventory().AddItem(name, 1, 1, 0, 0L, "");
                        if (item != null)
                            player.EquipItem(item, triggerEquipEffects: false);
                        else
                            NightTerrorsPlugin.Log.LogWarning($"NightTerrors: unknown kit item '{name}'");
                    }
                    NightTerrorsPlugin.Log.LogInfo($"NightTerrors: equipped kit [{string.Join(", ", kit)}].");
                    break;
                // KeepGear, SwapEquipment: inventory was already set before this RPC.
            }
        }

        // Server → specific client: you got someone else's inventory (swap scenario).
        static void ReceiveInventory(long sender, ZPackage pkg)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            byte[] bytes = pkg.ReadByteArray();
            player.UnequipAllItems();
            player.GetInventory().Load(new ZPackage(bytes));
            NightTerrorsPlugin.Log.LogInfo("NightTerrors: swapped inventory applied.");
        }

        // Server → All: restore your original inventory.
        static void RestoreInventory(long sender)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (NightTerrorsClient.SavedInventory == null) return;

            player.UnequipAllItems();
            player.GetInventory().Load(new ZPackage(NightTerrorsClient.SavedInventory));
            NightTerrorsClient.SavedInventory = null;
            NightTerrorsPlugin.Log.LogInfo("NightTerrors: inventory restored.");
        }

        // Server → All: event is over.
        static void EventEnd(long sender)
        {
            NightTerrorsClient.IsEventActive   = false;
            NightTerrorsClient.IsEventStarting = false;
            NightTerrorsPlugin.Log.LogInfo("NightTerrors: event ended on client.");
        }

        // Server → All (or specific client): force / clear weather.
        static void SetWeather(long sender, string envName)
        {
            if (EnvMan.instance == null) return;
            EnvMan.instance.SetForceEnvironment(envName);
            NightTerrorsPlugin.Log.LogInfo($"NightTerrors: weather set to '{envName}'.");
        }

        // ── Client → Server ─────────────────────────────────────────────────

        // Client sends inventory bytes up to the server (for swap scenario).
        static void UploadInventory(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            byte[] bytes = pkg.ReadByteArray();
            NightTerrorsEvent.CollectedInventories[sender] = bytes;
            NightTerrorsPlugin.Log.LogInfo($"NightTerrors: received inventory from peer {sender}.");
        }

        // Client reports its death to the server.
        static void PlayerDied(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            NightTerrorsPlugin.Log.LogInfo($"NightTerrors: peer {sender} died.");
            NightTerrorsEvent.OnPlayerDied(sender);
        }

        // Server → surviving client: time limit expired, die now.
        // Sent before EventEnd so tombstone/skill suppression patches are still active.
        static void KillSurvivor(long sender)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!NightTerrorsClient.IsEventActive) return;

            NightTerrorsPlugin.Log.LogInfo("NightTerrors: time limit — killing survivor.");
            var hit = new HitData();
            hit.m_damage.m_blunt = 99999f;
            hit.m_point = player.transform.position;
            player.Damage(hit);
        }
    }
}
