using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NightTerrors
{
    public static class NightTerrorsEvent
    {
        public static bool     IsActive;
        public static Scenario CurrentScenario;
        public static Vector3  ChosenDestination;

        public static HashSet<long>          AlivePeers           = new HashSet<long>();
        public static Dictionary<long, byte[]> CollectedInventories = new Dictionary<long, byte[]>();

        // ── Entry point ─────────────────────────────────────────────────────

        public static void Begin()
        {
            if (IsActive) return;

            // Pick scenario, destination.
            CurrentScenario   = ScenarioSelector.Choose(NightTerrorsPlugin.CfgScenarioWeights.Value);
            ChosenDestination = FindRandomPosition();
            if (ChosenDestination == Vector3.zero)
            {
                NightTerrorsPlugin.Log.LogWarning("NightTerrors: could not find a valid teleport position — aborting.");
                return;
            }

            // Populate alive peers (connected, non-ghost peers only).
            AlivePeers.Clear();
            CollectedInventories.Clear();
            var peers = ZNet.instance.GetPeers();
            foreach (var peer in peers)
            {
                if (peer.m_uid == 0) continue;
                AlivePeers.Add(peer.m_uid);
            }

            if (AlivePeers.Count == 0)
            {
                NightTerrorsPlugin.Log.LogWarning("NightTerrors: no valid peers — aborting.");
                return;
            }

            // Fall back to GoNaked if only one player and SwapEquipment was chosen.
            if (CurrentScenario == Scenario.SwapEquipment && AlivePeers.Count == 1)
                CurrentScenario = Scenario.GoNaked;

            IsActive = true;
            NightTerrorsPlugin.Log.LogInfo(
                $"NightTerrors: event starting — scenario={CurrentScenario}, peers={AlivePeers.Count}, dest={ChosenDestination}");

            // Tell clients to save their inventories (they will upload back for swap).
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_SaveInventory");

            // Run the rest of the sequence in a coroutine so we can yield.
            NightTerrorsPlugin.Instance.StartCoroutine(EventSequence());
        }

        static IEnumerator EventSequence()
        {
            // Wait for SaveInventory RPCs to arrive and clients to upload.
            yield return new WaitForSeconds(2.2f);

            // === Phase 1: Swap inventories if needed ===
            if (CurrentScenario == Scenario.SwapEquipment)
                ExecuteSwap();

            // === Phase 2: Build EventStart payload ===
            string[] kit = (CurrentScenario == Scenario.DifferentEquipment)
                ? ScenarioSelector.PickKit()
                : new string[0];

            var eventPkg = new ZPackage();
            eventPkg.Write((int)CurrentScenario);
            eventPkg.Write(NightTerrorsPlugin.CfgFriendlyFire.Value);
            eventPkg.Write(NightTerrorsPlugin.CfgMonsterCount.Value);
            eventPkg.Write(kit.Length);
            foreach (string s in kit) eventPkg.Write(s);

            // === Phase 3: Announce + weather + teleport ===
            Announce("YOU HAVE BEEN TAKEN", MessageHud.MessageType.Center);
            ApplyRandomWeather();

            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_EventStart", eventPkg);

            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.m_uid == 0) continue;
                TeleportPeer(peer, ChosenDestination);
            }

            if (NightTerrorsPlugin.CfgSpawnMonsters.Value)
                SpawnMonsters(ChosenDestination, NightTerrorsPlugin.CfgMonsterCount.Value);

            // === Phase 4: Time limit ===
            yield return new WaitForSeconds(NightTerrorsPlugin.CfgEventDuration.Value);
            if (IsActive)
            {
                NightTerrorsPlugin.Log.LogInfo("NightTerrors: time limit reached — ending event.");
                End();
            }
        }

        // ── Swap inventory redistribution ───────────────────────────────────

        static void ExecuteSwap()
        {
            var peerList = AlivePeers.ToList();
            // Simple shuffle.
            for (int i = peerList.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                long tmp = peerList[i]; peerList[i] = peerList[j]; peerList[j] = tmp;
            }

            // Rotate: peer[i] gets peer[(i+1) % n]'s inventory.
            int n = peerList.Count;
            for (int i = 0; i < n; i++)
            {
                long recipient = peerList[i];
                long donor     = peerList[(i + 1) % n];

                if (!CollectedInventories.TryGetValue(donor, out byte[] bytes))
                {
                    NightTerrorsPlugin.Log.LogWarning(
                        $"NightTerrors: no upload from peer {donor} — skipping swap for peer {recipient}.");
                    continue;
                }

                var pkg = new ZPackage();
                pkg.Write(bytes);
                ZRoutedRpc.instance.InvokeRoutedRPC(
                    recipient, "NightTerrors_ReceiveInventory", pkg);
            }
        }

        // ── Death tracking ──────────────────────────────────────────────────

        public static void OnPlayerDied(long peerUID)
        {
            if (!IsActive) return;
            AlivePeers.Remove(peerUID);
            NightTerrorsPlugin.Log.LogInfo(
                $"NightTerrors: {AlivePeers.Count} players still alive.");

            // Remove any peers that disconnected mid-event.
            var connected = new HashSet<long>(ZNet.instance.GetPeers().Select(p => p.m_uid));
            AlivePeers.RemoveWhere(uid => !connected.Contains(uid));

            if (AlivePeers.Count == 0)
                End();
        }

        // ── Event end ───────────────────────────────────────────────────────

        public static void End()
        {
            if (!IsActive) return;
            IsActive = false;

            NightTerrorsPlugin.Log.LogInfo("NightTerrors: ending event.");
            Announce("The darkness claims you.", MessageHud.MessageType.Center);

            // Clear forced weather.
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_SetWeather", "");

            // Restore original inventories.
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_RestoreInventory");

            // Kill any survivors (still in AlivePeers) before clearing the event flag
            // so tombstone and skill-loss suppression are still active on their death.
            foreach (long uid in AlivePeers)
            {
                NightTerrorsPlugin.Log.LogInfo($"NightTerrors: killing survivor {uid}.");
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, "NightTerrors_KillSurvivor");
            }

            // Clear client event flag.
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_EventEnd");

            AlivePeers.Clear();
            CollectedInventories.Clear();
        }

        // ── Teleportation ───────────────────────────────────────────────────

        static void TeleportPeer(ZNetPeer peer, Vector3 pos)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(
                peer.m_uid, "RPC_TeleportPlayer",
                pos, Quaternion.identity, true);
        }

        // ── Random world position ────────────────────────────────────────────

        static Vector3 FindRandomPosition()
        {
            float waterLevel = ZoneSystem.instance.m_waterLevel;

            for (int attempt = 0; attempt < 100; attempt++)
            {
                float x = UnityEngine.Random.Range(-10000f, 10000f);
                float z = UnityEngine.Random.Range(-10000f, 10000f);
                float y = WorldGenerator.instance.GetHeight(x, z);

                // Accept land or ocean equally — this mod is cruel.
                if (y > waterLevel - 20f)   // anything not absurdly deep
                    return new Vector3(x, Mathf.Max(y, waterLevel) + 1f, z);
            }

            NightTerrorsPlugin.Log.LogError("NightTerrors: FindRandomPosition failed after 100 attempts.");
            return Vector3.zero;
        }

        // ── Monster spawning ─────────────────────────────────────────────────

        static void SpawnMonsters(Vector3 pos, int count)
        {
            string[] candidates = GetMonstersForBiome(pos);
            for (int i = 0; i < count; i++)
            {
                string name = candidates[UnityEngine.Random.Range(0, candidates.Length)];
                int hash    = name.GetStableHashCode();
                Vector3 spawnPos = pos + new Vector3(
                    UnityEngine.Random.Range(-8f, 8f), 0f,
                    UnityEngine.Random.Range(-8f, 8f));

                ZRoutedRpc.instance.InvokeRoutedRPC(
                    ZRoutedRpc.Everybody, "SpawnObject",
                    spawnPos, Quaternion.identity, hash);
            }
        }

        static string[] GetMonstersForBiome(Vector3 pos)
        {
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos.x, pos.z);
            float y = WorldGenerator.instance.GetHeight(pos.x, pos.z);
            bool ocean = y < ZoneSystem.instance.m_waterLevel;

            if (ocean)                                return new[] { "Serpent" };
            if (biome == Heightmap.Biome.Meadows)     return new[] { "Greydwarf", "Neck" };
            if (biome == Heightmap.Biome.BlackForest) return new[] { "Troll", "Greydwarf_Elite" };
            if (biome == Heightmap.Biome.Swamp)       return new[] { "Draugr", "Blob", "Wraith" };
            if (biome == Heightmap.Biome.Mountain)    return new[] { "Drake", "Fenring" };
            if (biome == Heightmap.Biome.Plains)      return new[] { "Fuling", "Deathsquito" };
            if (biome == Heightmap.Biome.Mistlands)   return new[] { "Seeker", "Tick" };
            if (biome == Heightmap.Biome.AshLands)    return new[] { "Charred_Warrior", "Morgen" };
            return new[] { "Greydwarf", "Troll" };
        }

        // ── Weather ─────────────────────────────────────────────────────────

        static void ApplyRandomWeather()
        {
            string pool = NightTerrorsPlugin.CfgWeatherPool.Value;
            if (string.IsNullOrWhiteSpace(pool)) return;

            string[] options = pool.Split(',');
            string chosen    = options[UnityEngine.Random.Range(0, options.Length)].Trim();

            NightTerrorsPlugin.Log.LogInfo($"NightTerrors: forcing weather '{chosen}'.");
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "NightTerrors_SetWeather", chosen);
        }

        // ── Utility ─────────────────────────────────────────────────────────

        static void Announce(string text, MessageHud.MessageType type)
        {
            if (string.IsNullOrWhiteSpace(text) || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, "ShowMessage", (int)type, text);
        }
    }
}
