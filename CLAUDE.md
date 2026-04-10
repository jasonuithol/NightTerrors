# NightTerrors — Valheim Mod

## What This Mod Does

NightTerrors is a BepInEx mod for Valheim. When all players go to sleep, there is a
configurable chance (1-in-20 by default) that something terrible happens.

All sleeping players are yanked from their beds and teleported to a random location
anywhere in the world — any biome, including the ocean. A scenario is chosen at random:
players might keep their gear, be stripped naked, be given completely different equipment,
or have their inventories swapped with each other. Monsters may spawn. Friendly fire may
be on. The players are expected to die quickly.

Throughout the event, no skill penalty applies on death. When all players die they respawn
normally at their beds with their original inventories restored. The night passes as if
nothing happened.

---

## Project Name

`NightTerrors`

---

## Development Environment

See `VALHEIM_DEVELOPMENT_ENVIRONMENT.md` for the full reference. Key points:

- Claude Code runs inside a Podman container. The host watcher **must** be running before
  issuing any commands:
  ```
  ~/Projects/claude-sandbox/valheim-watcher.sh
  ```
- All commands are issued by creating files in `/workspace/valheim/commands/`
- Build and deploy commands are **blocking** — poll for result files before proceeding
- Always **delete result files** after reading them or they'll confuse the next run

### Build & Deploy

```bash
# Build
echo "NightTerrors" > /workspace/valheim/commands/build
while [ ! -f /workspace/valheim/commands/build-done ] && \
      [ ! -f /workspace/valheim/commands/build-failed ]; do sleep 2; done
if [ -f /workspace/valheim/commands/build-done ]; then
    echo "Build OK"; rm /workspace/valheim/commands/build-done
else
    echo "Build FAILED — check /workspace/valheim/logs/build.log"
    rm /workspace/valheim/commands/build-failed; exit 1
fi

# Deploy to server
echo "NightTerrors" > /workspace/valheim/commands/deploy-server
while [ ! -f /workspace/valheim/commands/deploy-server-done ] && \
      [ ! -f /workspace/valheim/commands/deploy-server-failed ]; do sleep 2; done
rm -f /workspace/valheim/commands/deploy-server-done /workspace/valheim/commands/deploy-server-failed

# Deploy to client
echo "NightTerrors" > /workspace/valheim/commands/deploy-client
while [ ! -f /workspace/valheim/commands/deploy-client-done ] && \
      [ ! -f /workspace/valheim/commands/deploy-client-failed ]; do sleep 2; done
rm -f /workspace/valheim/commands/deploy-client-done /workspace/valheim/commands/deploy-client-failed
```

### Restart Server / Client

```bash
touch /workspace/valheim/commands/stop-server
sleep 3
touch /workspace/valheim/commands/start-server

# Check startup
sleep 5
tail -30 /workspace/valheim/server/BepInEx/LogOutput.log | grep -i "nightterrors\|error\|exception"
```

### Log Paths

| Log | Path |
|-----|------|
| Server BepInEx | `/workspace/valheim/server/BepInEx/LogOutput.log` |
| Client BepInEx | `/workspace/valheim/client/BepInEx/LogOutput.log` |
| Build | `/workspace/valheim/logs/build.log` |
| Deploy-server | `/workspace/valheim/logs/deploy-server.log` |
| Deploy-client | `/workspace/valheim/logs/deploy-client.log` |

### Decompiling Game Code

**Before writing any Harmony patch, decompile the target class first.** Harmony matches
parameters by name — guessing is wrong.

```bash
echo "/workspace/valheim/server/valheim_server_Data/Managed/assembly_valheim.dll" \
  > /workspace/valheim/commands/ilspy

while [ ! -f /workspace/valheim/commands/ilspy-done ] && \
      [ ! -f /workspace/valheim/commands/ilspy-failed ]; do sleep 2; done
rm -f /workspace/valheim/commands/ilspy-done /workspace/valheim/commands/ilspy-failed

# Then grep the output
grep -A 20 "SkipToMorning" /workspace/valheim/logs/ilspy.log
```

---

## Project Structure

```
/workspace/NightTerrors/
├── NightTerrors.csproj
├── Plugin.cs                ← BepInEx entry point, config binding
├── NightTerrorsEvent.cs     ← Server-side state machine, event lifecycle
├── Scenarios.cs             ← Scenario definitions and selection logic
├── Patches.cs               ← All Harmony patches
├── RpcHandler.cs            ← Custom RPC registration and dispatch
├── deploy-server.sh
├── deploy-client.sh
└── ThunderstoreAssets/
    ├── manifest.json
    ├── README.md
    └── icon.svg             (convert: echo "path/to/icon.svg" > commands/svg-to-png)
```

---

## csproj

Standard template from MODDING_WISDOM_RAINDANCE.md. Critical settings:

- `AssemblyName`: `NightTerrors`
- `PluginGUID`: `nightterrors` → config file will be `nightterrors.cfg` (keep GUID simple)
- `TargetFramework`: `netstandard2.1` — do NOT use `net462` on Linux
- `LangVersion`: `8.0`
- No NuGet packages, no custom `OutputPath`
- Suppress `MSB3277` with `<MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>`

References needed: `BepInEx`, `0Harmony`, `assembly_valheim`, `UnityEngine`,
`UnityEngine.CoreModule`.

---

## Plugin Entry Point (Plugin.cs)

```csharp
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class NightTerrorsPlugin : BaseUnityPlugin
{
    public const string PluginGUID    = "nightterrors";
    public const string PluginName    = "NightTerrors";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Log;
    internal static NightTerrorsPlugin Instance;
    private Harmony _harmony;

    internal static ConfigEntry<int>    CfgTriggerChance;
    internal static ConfigEntry<bool>   CfgFriendlyFire;
    internal static ConfigEntry<bool>   CfgSpawnMonsters;
    internal static ConfigEntry<int>    CfgMonsterCount;
    internal static ConfigEntry<string> CfgScenarioWeights;

    void Awake()
    {
        Log      = Logger;
        Instance = this;

        CfgTriggerChance = Config.Bind("General", "TriggerChance", 20,
            "1-in-N chance of the event triggering each time everyone sleeps.");
        CfgFriendlyFire = Config.Bind("General", "FriendlyFire", false,
            "Allow players to damage each other during the event.");
        CfgSpawnMonsters = Config.Bind("General", "SpawnMonsters", true,
            "Spawn monsters at the teleport location.");
        CfgMonsterCount = Config.Bind("General", "MonsterCount", 3,
            "Number of monsters to spawn.");
        CfgScenarioWeights = Config.Bind("General", "ScenarioWeights", "1,1,1,1",
            "Comma-separated weights for: KeepGear, GoNaked, DifferentEquipment, SwapEquipment.");

        _harmony = new Harmony(PluginGUID);
        _harmony.PatchAll();
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
    }

    void OnDestroy() => _harmony?.UnpatchSelf();
}
```

---

## This Mod Runs on Both Server AND Client

**The DLL must be deployed to both server and client.** The mod has:

- **Server-side logic**: event trigger, chance roll, scenario selection, peer tracking,
  death counting, monster spawning, RPC coordination.
- **Client-side logic**: inventory save/restore, equipment manipulation, skill loss
  suppression, friendly fire patch.

Guard server logic with:
```csharp
if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
```

Guard client-only logic with `Player.m_localPlayer != null` checks.

---

## Architecture

### Server-Side State (NightTerrorsEvent.cs)

```csharp
public static class NightTerrorsEvent
{
    public static bool IsActive;
    public static Scenario CurrentScenario;
    public static Vector3 ChosenDestination;  // all players go here

    // Keyed by peer UID
    public static HashSet<long> AlivePeers = new();

    // For SwapEquipment: uid → inventory bytes received from another peer
    public static Dictionary<long, byte[]> CollectedInventories = new();  // upload staging
}
```

> **Note on BedPositions**: an earlier design tracked bed positions server-side, but
> Valheim respawns players at their set spawn point (bed) automatically — the server does
> not need to know bed positions. `SavedBedPosition` on the client is similarly unused.
> Both have been removed from the state structs to avoid confusion. If a future feature
> needs them, clients can look up `Player.m_localPlayer.GetSpawnPoint()` on demand.

### Client-Side State (RpcHandler.cs or a ClientState class)

```csharp
public static class NightTerrorsClient
{
    public static bool   IsEventActive;
    public static byte[] SavedInventory;   // ZPackage bytes, null when not in event
}
```

---

## Sleep Detection (Patches.cs)

Hook `EnvMan.SkipToMorning` — this fires on the server when all players have
successfully slept. It is the correct, authoritative trigger.

```csharp
[HarmonyPatch(typeof(EnvMan), "SkipToMorning")]
static class Patch_SkipToMorning
{
    static bool Prefix()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

        int chance = NightTerrorsPlugin.CfgTriggerChance.Value;
        if (UnityEngine.Random.Range(0, chance) != 0) return true; // normal sleep

        NightTerrorsEvent.Begin();
        return true; // let morning arrive normally — players just won't be here for it
    }
}
```

> **Note**: Returning `true` lets `SkipToMorning` proceed — morning comes, players are
> just elsewhere. This is simpler than suppressing time. When they die they respawn at
> their beds as normal. Only suppress `SkipToMorning` (return false) if you want to
> hold time frozen during the event — verify via decompile whether that causes issues.

**Decompile `EnvMan` first** to confirm `SkipToMorning` is the right hook and check its
exact signature. Also look for `SleepStart`, `SleepStop`, `Sleep` — there may be a better
entry point.

---

## Event Lifecycle (NightTerrorsEvent.cs)

```
Begin()
  ├─ Record bed positions for all peers
  ├─ Choose scenario
  ├─ Choose weather (random from configured list)
  ├─ RPC: NightTerrors_SaveInventory → all clients (clients save their own inventory)
  ├─ [If SwapEquipment] coordinate inventory exchange via RPCs
  ├─ RPC: NightTerrors_EventStart(scenario, friendlyFire, monsterCount) → all clients
  ├─ RPC: NightTerrors_SetWeather(environmentName) → all clients
  ├─ Teleport all peers to random world position
  ├─ Spawn monsters at/near that position
  └─ Mark IsActive = true, populate AlivePeers

OnPlayerDied(peerUID)   ← called when server receives NightTerrors_PlayerDied RPC
  ├─ Remove from AlivePeers
  └─ If AlivePeers is empty → End()

End()
  ├─ RPC: NightTerrors_SetWeather("") → all clients (clears forced weather, returns to natural)
  ├─ RPC: NightTerrors_RestoreInventory → all clients (clients restore saved inventory)
  ├─ RPC: NightTerrors_EventEnd → all clients (clear client active flag)
  └─ Mark IsActive = false
```

---

## Inventory Save / Restore (Client-Side)

Inventory manipulation must happen on the client because `Player.m_inventory` lives
in the client process. The server coordinates timing via RPCs.

```csharp
// Called when client receives NightTerrors_SaveInventory RPC
static void OnSaveInventory()
{
    var pkg = new ZPackage();
    Player.m_localPlayer.m_inventory.Save(pkg);
    NightTerrorsClient.SavedInventory = pkg.GetArray();
    NightTerrorsClient.SavedBedPosition = Player.m_localPlayer.GetSpawnPoint(); // verify API name
    Plugin.Log.LogInfo("NightTerrors: inventory saved.");
}

// Called when client receives NightTerrors_RestoreInventory RPC
static void OnRestoreInventory()
{
    if (NightTerrorsClient.SavedInventory == null) return;
    var pkg = new ZPackage(NightTerrorsClient.SavedInventory);
    Player.m_localPlayer.m_inventory.Load(pkg);
    NightTerrorsClient.SavedInventory = null;
    Plugin.Log.LogInfo("NightTerrors: inventory restored.");
}
```

**Decompile `Inventory` and `Player`** to verify:
- `Inventory.Save(ZPackage)` — confirm it exists and serialises all items
- `Inventory.Load(ZPackage)` — confirm it replaces contents entirely
- `Player.GetSpawnPoint()` or whatever stores the current bed spawn — confirm field/method name

---

## Teleportation

```csharp
// Server-side: teleport a peer to a position
static void TeleportPeer(ZNetPeer peer, Vector3 position)
{
    ZRoutedRpc.instance.InvokeRoutedRPC(
        peer.m_uid,
        "RPC_TeleportPlayer",
        position,
        Quaternion.identity,
        true  // distant = true for cross-zone teleport
    );
}
```

**Decompile `Player`** to confirm the `RPC_TeleportPlayer` signature — especially the
third parameter (bool? float? Quaternion separate?). Do not guess.

### Finding a Random World Position

Use `WorldGenerator` APIs to sample terrain. Options:

- **Random land**: generate a random (x, z), check `WorldGenerator.instance.GetHeight(x, z)`
  is above sea level (`~= 30f`). Retry until valid.
- **Specific biome**: use `WorldGenerator.instance.GetBiome(x, z)` to filter.
- **Ocean**: find a position where height is below sea level.

Decompile `WorldGenerator` and `Minimap` for exact method names. The world radius is
`10500f` — stay within it.

```csharp
// Sketch — verify all method names via decompile
static Vector3 FindRandomPosition(bool requireOcean = false)
{
    for (int i = 0; i < 100; i++)
    {
        float x = UnityEngine.Random.Range(-10000f, 10000f);
        float z = UnityEngine.Random.Range(-10000f, 10000f);
        float y = WorldGenerator.instance.GetHeight(x, z);

        bool isOcean = y < ZoneSystem.instance.m_waterLevel;  // verify field name
        if (requireOcean == isOcean)
            return new Vector3(x, y + 1f, z);
    }
    return Vector3.zero; // fallback — log a warning if this happens
}
```

---

## Scenarios (Scenarios.cs)

```csharp
public enum Scenario
{
    KeepGear          = 0,
    GoNaked           = 1,
    DifferentEquipment = 2,
    SwapEquipment     = 3,
}
```

Scenario is chosen server-side using weighted random selection from `CfgScenarioWeights`.
The chosen scenario is sent to all clients in the `NightTerrors_EventStart` RPC.

### KeepGear
No inventory changes. Clients do nothing special.

### GoNaked
Client strips all items on receiving `EventStart` with `scenario = GoNaked`:
```csharp
static void StripPlayer()
{
    var player = Player.m_localPlayer;
    // Decompile Player to find UnequipAllItems or iterate and call UnequipItem
    // Items remain in inventory but are unequipped — inventory was already saved
}
```
Decompile `Player` for exact unequip API. Likely `player.UnequipAllItems()` or iterating
`m_inventory.GetAllItems()` and calling `player.UnequipItem(item, false)`.

For fully naked (items removed, not just unequipped): clear the inventory after saving.
Saved inventory is already captured — clearing is safe.

### DifferentEquipment
Server picks a "kit" (a list of item prefab names) themed to the situation. Sends the
list in the RPC. Client clears their inventory and spawns + equips the kit items.

Kit ideas:
- `["Club", "ShieldWood"]` — peasant starter kit
- `["SwordBlackmetal", "ArmorPadded*"]` — endgame gear on a fresh character
- `["Torch"]` — just a torch, good luck
- `["FishingRod", "BaitOcean"]` — specifically for ocean teleports

**Decompile `ObjectDB` / `ZNetScene`** for how to spawn items by prefab name and add to
inventory. Look at how the game gives players starting items.

### SwapEquipment
Server-side coordination required. **The server has no direct access to client inventory
bytes** — clients must upload their saved inventory to the server before the server can
redistribute. This requires an additional Client→Server RPC.

Full flow:
1. Server sends `NightTerrors_SaveInventory` to all clients.
2. **Server waits** — clients save their inventory, then each client sends it back via
   `NightTerrors_UploadInventory` (Client → Server, payload: `ZPackage` with their bytes).
3. Server accumulates uploads. Once all expected peers have uploaded (or a timeout fires),
   server shuffles the list and sends each peer someone else's bytes via
   `NightTerrors_ReceiveInventory`.
4. Each client replaces their inventory with the received bytes.

The simplest timeout approach: after sending `SaveInventory`, wait a fixed 1.5s then
proceed with however many uploads arrived. Any peer that didn't upload in time keeps their
own inventory (i.e. effectively KeepGear for them).

With only 2 players this is a direct swap. With N players it's a rotation.
With 1 player, fall back to `GoNaked`.

Add to the RPC table and `ZRoutedRpc.Awake` registration:
```
NightTerrors_UploadInventory  Client → Server  ZPackage: byte[] inventory data
```

---

## No Skill Penalty

Patch `Skills.OnDeath` on the client to suppress skill loss when the event is active:

```csharp
[HarmonyPatch(typeof(Skills), "OnDeath")]
static class Patch_Skills_OnDeath
{
    static bool Prefix()
    {
        if (NightTerrorsClient.IsEventActive)
        {
            Plugin.Log.LogInfo("NightTerrors: skill loss suppressed.");
            return false; // skip the original method entirely
        }
        return true;
    }
}
```

`IsEventActive` is set when the client receives `NightTerrors_EventStart` and cleared
on `NightTerrors_EventEnd`.

**Decompile `Skills`** to confirm the method is named `OnDeath` and has no parameters
that need matching.

Alternative: check whether the game has a built-in world modifier for skill loss
(`World.m_noSkillLoss` or similar). If it exists, toggling it server-side may be
cleaner than patching.

---

## Death Handling (Patches.cs)

```csharp
[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
static class Patch_Player_OnDeath
{
    static void Postfix(Player __instance)
    {
        if (__instance != Player.m_localPlayer) return;
        if (!NightTerrorsClient.IsEventActive) return;

        Plugin.Log.LogInfo("NightTerrors: local player died during event.");

        // Notify server
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),  // verify method name via decompile
            "NightTerrors_PlayerDied",
            new ZPackage()
        );

        // Inventory restore happens when server sends RestoreInventory RPC after all die
        // (or immediately — decide based on whether multi-death ordering matters)
    }
}
```

**Important**: Normal Valheim respawn logic will respawn the player at their bed
(their set spawn point). As long as we do not change their spawn point during the event,
respawn is handled for free. Verify via decompile of `Player.OnDeath` and `Game.RequestRespawn`.

---

## Friendly Fire (Patches.cs)

Valheim normally prevents player-vs-player damage. Find the check via decompile:

```bash
grep -A 30 "IsFriend\|friendly\|pvp\|Player.*Damage" /workspace/valheim/logs/ilspy.log
```

The check is likely in `Character.Damage`, `Player.Damage`, or `IsDamagable`. Patch the
relevant method to allow damage between players when `IsEventActive` and `CfgFriendlyFire`
are both true:

```csharp
// Example — actual patch depends on decompile results
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
static class Patch_FriendlyFire
{
    static void Prefix(ref HitData hit, Character __instance)
    {
        // If event active + friendly fire on, remove the flag that blocks PvP
        // Exact implementation depends on how the game gates PvP — must decompile first
    }
}
```

This patch runs on the **client** (damage is processed locally).

---

## Monster Spawning (NightTerrorsEvent.cs)

Spawn monsters near the teleport position after players arrive. Use server-side spawning:

```csharp
static void SpawnMonsters(Vector3 position, int count)
{
    string[] candidates = GetMonstersForPosition(position);

    for (int i = 0; i < count; i++)
    {
        string prefabName = candidates[UnityEngine.Random.Range(0, candidates.Length)];
        int hash = prefabName.GetStableHashCode();

        Vector3 spawnPos = position + new Vector3(
            UnityEngine.Random.Range(-8f, 8f), 0f,
            UnityEngine.Random.Range(-8f, 8f));

        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.Everybody,
            "SpawnObject",
            spawnPos,
            Quaternion.identity,
            hash
        );
    }
}

static string[] GetMonstersForPosition(Vector3 pos)
{
    // Check biome at position and return appropriate monsters
    // Ocean: "Serpent" is perfect
    // Meadows: "Greydwarf", "Neck"
    // Black Forest: "Troll", "Greydwarf_Elite"
    // Swamp: "Draugr", "Blob", "Wraith"
    // Mountains: "Drake", "Fenring"
    // Plains: "Fuling", "Deathsquito"
    float height = WorldGenerator.instance.GetHeight(pos.x, pos.z);
    Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos.x, pos.z); // verify API
    // ...
}
```

**Decompile `ZRoutedRpc`** to confirm `SpawnObject` exists with this signature. Also look
at `RandEventSystem` — it handles raid spawning and may expose a cleaner API.

---

## Custom RPCs (RpcHandler.cs)

Register ALL RPCs in a single `ZRoutedRpc.Awake` postfix. Both server and client register
all handlers — guards inside each handler determine who acts.

```csharp
[HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.Awake))]
static class Patch_ZRoutedRpc_Awake
{
    static void Postfix()
    {
        ZRoutedRpc.instance.Register("NightTerrors_SaveInventory",    new Action<long>(RPC_SaveInventory));
        ZRoutedRpc.instance.Register<ZPackage>("NightTerrors_EventStart",       RPC_EventStart);
        ZRoutedRpc.instance.Register<ZPackage>("NightTerrors_ReceiveInventory",  RPC_ReceiveInventory);
        ZRoutedRpc.instance.Register<ZPackage>("NightTerrors_UploadInventory",   RPC_UploadInventory);
        ZRoutedRpc.instance.Register<string>("NightTerrors_SetWeather",          RPC_SetWeather);  // was missing
        ZRoutedRpc.instance.Register("NightTerrors_RestoreInventory",  new Action<long>(RPC_RestoreInventory));
        ZRoutedRpc.instance.Register("NightTerrors_EventEnd",          new Action<long>(RPC_EventEnd));
        ZRoutedRpc.instance.Register("NightTerrors_PlayerDied",        new Action<long>(RPC_PlayerDied));
    }
}
```

### RPC Summary

| RPC Name | Direction | Payload | Purpose |
|---|---|---|---|
| `NightTerrors_SaveInventory` | Server → All | none | Clients save their own inventory NOW |
| `NightTerrors_UploadInventory` | Client → Server | ZPackage: byte[] inventory data | Client sends saved bytes to server (swap scenario) |
| `NightTerrors_EventStart` | Server → All | ZPackage: scenario(int), friendlyFire(bool), monsterCount(int) | Begin event on clients |
| `NightTerrors_SetWeather` | Server → All | string environmentName | Force weather on each client's EnvMan; pass "" to clear |
| `NightTerrors_ReceiveInventory` | Server → Client | ZPackage: byte[] inventory data | Server redistributes inventory (swap scenario) |
| `NightTerrors_RestoreInventory` | Server → All | none | Clients restore saved inventory |
| `NightTerrors_EventEnd` | Server → All | none | Clear client active flag |
| `NightTerrors_PlayerDied` | Client → Server | none | Client reporting their death |

All RPC names prefixed with `NightTerrors_` to avoid collisions with other mods.

### Sending from Server

```csharp
// Broadcast to all
ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "NightTerrors_EventEnd");

// Send to specific peer
ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "NightTerrors_ReceiveInventory", pkg);

// Client → server
ZRoutedRpc.instance.InvokeRoutedRPC(
    ZRoutedRpc.instance.GetServerPeerID(), // verify method name
    "NightTerrors_PlayerDied"
);
```

---

## Jumpscare (NightTerrorsEvent.cs)

Before teleporting, the server spawns a real Wraith near each player. The Wraith's
natural charge AI flies it straight at the nearest player (the one it just spawned next
to). Two seconds later the teleport fires and the Wraith is left behind — or despawns
naturally in the unloaded zone.

No custom assets, no camera manipulation. The Wraith does the work.

### Timing Sequence

```
Begin()
  ...
  ├─ Announce: "The Mara has found you..." (TopLeft, subtle)
  ├─ SpawnObject: one Wraith per player, ~12m away facing them
  ├─ Wait 2.2 seconds        ← Wraith is mid-charge, filling the screen
  ├─ Announce: "YOU HAVE BEEN TAKEN" (Center, dramatic)
  ├─ RPC: NightTerrors_SetWeather → all clients
  └─ Teleport all peers
```

The 2.2s window is tunable. Long enough for the charge to register; short enough that
the Wraith hasn't connected. Adjust after playtesting.

### Server-Side Implementation

Do **not** use the `SpawnObject` RPC here — it is fire-and-forget and returns no handle.
To despawn the Wraiths after the teleport we need to own their ZDOs. Use
`ZNetScene.instance.SpawnObject()` directly on the server instead, which returns a
`GameObject`. Grab the `ZNetView` from it and store the ZDOID for later destruction.

```csharp
// Store spawned Wraith ZDOIDs so we can destroy them after teleport
static readonly List<ZDOID> _jumpscareZdoids = new();

static void SpawnJumpscareWraiths()
{
    _jumpscareZdoids.Clear();

    string wraithPrefab = "Wraith"; // verify name via decompile
    GameObject prefab = ZNetScene.instance.GetPrefab(wraithPrefab);
    if (prefab == null)
    {
        Plugin.Log.LogWarning($"NightTerrors: could not find prefab '{wraithPrefab}' — skipping jumpscare.");
        return;
    }

    foreach (var peer in ZNet.instance.GetPeers())
    {
        if (peer.m_uid == 0) continue;

        Vector3 playerPos = peer.m_refPos;
        Vector2 dir2d = UnityEngine.Random.insideUnitCircle.normalized;
        Vector3 spawnPos = playerPos + new Vector3(dir2d.x, 1.5f, dir2d.y) * 12f;
        Quaternion facing = Quaternion.LookRotation(playerPos - spawnPos);

        GameObject spawned = ZNetScene.instance.SpawnObject(prefab, spawnPos, facing); // verify signature
        if (spawned == null) continue;

        ZNetView znv = spawned.GetComponent<ZNetView>();
        if (znv != null && znv.IsValid())
        {
            _jumpscareZdoids.Add(znv.GetZDO().m_uid);
        }
    }
}

static void DespawnJumpscareWraiths()
{
    foreach (var zdoid in _jumpscareZdoids)
    {
        ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
        if (zdo != null)
            ZNetScene.instance.Destroy(zdo.m_uid); // verify exact Destroy signature
    }
    _jumpscareZdoids.Clear();
}
```

Then the coroutine sequence:

```csharp
Plugin.Instance.StartCoroutine(JumpscareSequence());

private static IEnumerator JumpscareSequence()
{
    // Phase 1: announce and spawn
    Announce("The Mara has found you...", MessageHud.MessageType.TopLeft);
    SpawnJumpscareWraiths();

    // Phase 2: let the Wraiths charge
    yield return new WaitForSeconds(2.2f);

    // Phase 3: dramatic message, weather, teleport
    Announce("YOU HAVE BEEN TAKEN", MessageHud.MessageType.Center);
    ApplyRandomWeather();
    foreach (var peer in ZNet.instance.GetPeers())
    {
        if (peer.m_uid == 0) continue;
        TeleportPeer(peer, _chosenDestination);
    }

    // Phase 4: kill the Wraiths now that no one is there to see them anyway
    yield return new WaitForSeconds(0.5f); // tiny grace period for teleport RPCs to send
    DespawnJumpscareWraiths();
}
```

`_chosenDestination` must be computed in `Begin()` and stored statically before the
coroutine starts — all players go to the same location so they can suffer together.

### Things to Verify via Decompile

- **Wraith prefab name** — almost certainly `"Wraith"` but confirm:
  ```bash
  grep -i "wraith" /workspace/valheim/logs/ilspy.log
  ```
  Look for the prefab registration in `ZNetScene` or `ObjectDB`.

- **`ZNetScene.SpawnObject` signature** — decompile `ZNetScene` to find the correct
  overload. It likely takes `(GameObject prefab, Vector3 pos, Quaternion rot)` but
  there may be multiple overloads. Do not guess.

- **`ZNetScene.Destroy` signature** — may be `Destroy(ZDOID)` or `Destroy(ZDO)`.
  Decompile to confirm. This is what propagates the destruction to all clients.

- **Wraith AI aggro range** — if the Wraith doesn't auto-aggro at 12m, reduce the spawn
  distance. Check `MonsterAI` / `BaseAI` for `m_alertRange` on the Wraith prefab.

- **`peer.m_refPos` freshness** — last-known position synced by the client. Slightly
  stale but good enough for a jumpscare spawn offset.

### Notes

- One Wraith per player means 4 players = 4 Wraiths. All players see all of them. Good.
- The Wraiths are destroyed 0.5s after the teleport RPCs go out. Players will be gone;
  the Wraiths vanish cleanly from the world without haunting the server's zone.
- If `ZNetScene.Destroy` turns out not to exist or not to work as expected, the fallback
  is to call `ZDOMan.instance.DestroyZDO(zdoid)` — decompile both to find the right one.
- The Wraith may deal damage in the 2.2s window. **Skill loss is NOT suppressed during
  this window.** `IsEventActive` is the *client-side* flag set only when clients receive
  `NightTerrors_EventStart`, which isn't sent until after the 2.2s wait. A Wraith kill
  during the jumpscare will apply skill loss. Two options to fix this:
  - Option A: send a lightweight `NightTerrors_EventBeginning` RPC (no payload) before
    spawning the Wraiths; clients set `IsEventActive = true` on receipt.
  - Option B: accept it — the jumpscare window is short and Wraith kills are unlikely.
  The current design goes with Option B. If playtesting shows it matters, implement A.

---

## Weather (NightTerrorsEvent.cs + RpcHandler.cs)

### Why it needs an RPC

`EnvMan` is **client-local** — each client runs its own weather simulation independently.
Calling `EnvMan.instance.SetForceEnvironment("Ashrain")` on the server only affects the
server process, where there are no players. To force weather on all players, the server
must send an RPC and each client calls it on their own `EnvMan` instance.

This is explicitly documented as a gotcha in the wisdom docs. Do not attempt to drive
weather from the server directly.

### Config

Add to `Plugin.cs` `Awake()`:
```csharp
internal static ConfigEntry<string> CfgWeatherPool;

CfgWeatherPool = Config.Bind("Weather", "WeatherPool",
    "ThunderStorm,Ashrain,Snow,Twilight_Snow,DeepForest_Mist,SwampRain,Mistlands_darkening",
    "Comma-separated list of environment names to pick from randomly. " +
    "Set to empty string to disable weather effects.");
```

### Server-Side: Pick and Send

```csharp
static void ApplyRandomWeather()
{
    string pool = NightTerrorsPlugin.CfgWeatherPool.Value;
    if (string.IsNullOrWhiteSpace(pool)) return;

    string[] options = pool.Split(',');
    string chosen = options[UnityEngine.Random.Range(0, options.Length)].Trim();

    Plugin.Log.LogInfo($"NightTerrors: forcing weather '{chosen}' on all clients.");

    ZRoutedRpc.instance.InvokeRoutedRPC(
        ZRoutedRpc.Everybody,
        "NightTerrors_SetWeather",
        chosen
    );
}

// Call ApplyRandomWeather() inside Begin(), after the EventStart RPC.
// Call it again with "" inside End() to restore natural weather.
```

### Client-Side RPC Handler

```csharp
static void RPC_SetWeather(long sender, string environmentName)
{
    if (EnvMan.instance == null) return;

    // Use SetForceEnvironment(), NOT direct m_forceEnv assignment.
    // The method also triggers FixedUpdate() and ReflectionUpdate immediately.
    EnvMan.instance.SetForceEnvironment(environmentName);

    Plugin.Log.LogInfo($"NightTerrors: weather set to '{environmentName}'.");
}
```

Register in the `ZRoutedRpc.Awake` patch:
```csharp
ZRoutedRpc.instance.Register<string>("NightTerrors_SetWeather", RPC_SetWeather);
```

### Known Good Environment Names

These are confirmed present in Valheim's environment list. The full list can be extracted
by decompiling `EnvMan` and looking at `m_environments`:

| Name | Effect |
|---|---|
| `"ThunderStorm"` | Heavy rain, lightning, darkness |
| `"LightRain"` | Light drizzle |
| `"Rain"` | Standard rain |
| `"Snow"` | Mountain snowfall |
| `"Twilight_Snow"` | Blizzard — dark, heavy snow, very disorienting |
| `"Ashrain"` | Ashlands ash storm — orange sky, ash particles |
| `"SwampRain"` | Swamp atmosphere — green-grey, oppressive |
| `"DeepForest_Mist"` | Black Forest mist — low visibility |
| `"Mistlands_darkening"` | Mistlands — dense blue-grey mist |
| `"Crypt"` | Cave/dungeon atmosphere |

**Get the authoritative full list** by decompiling `EnvMan`:
```bash
echo "/workspace/valheim/server/valheim_server_Data/Managed/assembly_valheim.dll" \
  > /workspace/valheim/commands/ilspy
# then:
grep "m_name\|Env\b" /workspace/valheim/logs/ilspy.log | grep -i "environ\|thunder\|rain\|snow\|mist\|ash\|swamp"
```

Names are case-sensitive. An unrecognised name silently falls back to default weather —
log the chosen name so bad config values are obvious in the log.

### Clearing Weather on Event End

```csharp
// In End(), before EventEnd RPC:
ZRoutedRpc.instance.InvokeRoutedRPC(
    ZRoutedRpc.Everybody,
    "NightTerrors_SetWeather",
    ""  // empty string clears the override, returns to natural weather
);
```

> **Verify via decompile**: confirm that `EnvMan.SetForceEnvironment("")` clears the
> override without throwing. If it requires a non-empty string, use a sentinel like
> `"Clear"` or null-check inside `RPC_SetWeather` and call a different method to unset.

---

## Player Messages

Use `ShowMessage` for all player-facing text — NOT `ChatMessage` (requires real platform ID,
fragile, avoid).

```csharp
static void Announce(string text, MessageHud.MessageType type = MessageHud.MessageType.Center)
{
    if (string.IsNullOrWhiteSpace(text)) return;
    if (ZRoutedRpc.instance == null) return;
    ZRoutedRpc.instance.InvokeRoutedRPC(
        ZRoutedRpc.Everybody,
        "ShowMessage",
        (int)type,
        text
    );
}
```

Suggested messages:
- `"Something stirs in the dark..."` (before teleport, `TopLeft`)
- `"YOU HAVE BEEN TAKEN"` (on teleport, `Center`)
- `"The darkness claims you."` (all dead, `Center`)

---

## Peer Iteration (Server-Side)

```csharp
foreach (var peer in ZNet.instance.GetPeers())
{
    if (peer.m_uid == 0) continue; // always skip ghost peers

    ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
    if (zdo == null) continue;

    // peer.m_uid         — unique ID (long)
    // peer.m_playerName  — display name
    // peer.m_refPos      — last known position (use for bed recording)
}
```

**Cleanup** — when event ends, remove disconnected peers from tracking dicts:
```csharp
var connected = new HashSet<long>(ZNet.instance.GetPeers().Select(p => p.m_uid));
NightTerrorsEvent.AlivePeers.RemoveWhere(uid => !connected.Contains(uid));
```

---

## Deploy Scripts

### deploy-server.sh

```bash
#!/bin/bash
set -e
TARGET="/workspace/valheim/server/BepInEx/plugins"
mkdir -p "$TARGET"
cp bin/Release/netstandard2.1/NightTerrors.dll "$TARGET/"
echo "Deployed NightTerrors to server"
```

### deploy-client.sh

```bash
#!/bin/bash
set -e
TARGET="/workspace/valheim/client/BepInEx/plugins"
mkdir -p "$TARGET"
cp bin/Release/netstandard2.1/NightTerrors.dll "$TARGET/"
echo "Deployed NightTerrors to client"
```

---

## Things to Decompile Before Writing Code

Do these decompiles at the start of the session, before writing any patch. The output
goes to `/workspace/valheim/logs/ilspy.log` — grep it for what you need.

| Class | What to look for |
|---|---|
| `EnvMan` | `SkipToMorning` signature; `SetForceEnvironment` signature; full `m_environments` list for valid weather names |
| `Player` | `OnDeath`, `OnRespawn`, `GetSpawnPoint`, `UnequipAllItems`, `UnequipItem` |
| `Skills` | `OnDeath` signature (skill loss on death) |
| `Character` | `Damage` — find the friendly-fire / PvP gate |
| `Inventory` | `Save`, `Load`, `AddItem`, `GetAllItems`, `RemoveAll` |
| `WorldGenerator` | `GetHeight`, `GetBiome`, coordinate range of the world |
| `ZRoutedRpc` | `RPC_TeleportPlayer` signature; `GetServerPeerID` or equivalent |
| `ZoneSystem` | `m_waterLevel` or sea level constant |
| `RandEventSystem` | Monster spawning for events |

---

## Known Gotchas (from modding wisdom)

1. **Ghost peers** — always `if (peer.m_uid == 0) continue` when iterating peers
2. **`ShowMessage` not `ChatMessage`** — ChatMessage needs real platform IDs, it throws `EndOfStreamException` with fake ones
3. **Equipment ZDO values are int hashes** — use `zdo.GetInt(ZDOVars.s_rightItem)`, NOT `GetString`
4. **Emote ZDO IS a string** — `zdo.GetString(ZDOVars.s_emote)` is correct (exception to above)
5. **`CS0108` / BroadcastMessage name clash** — `Component.BroadcastMessage` exists on all MonoBehaviours; rename any method that would collide
6. **`mkdir -p` in deploy scripts** — the plugins subdirectory may not exist on first deploy
7. **`~` doesn't expand in double-quoted bash strings** — use `$HOME` or hardcode the path
8. **`StopEmote` fires every frame** — if you ever patch it, debounce with a flag
9. **`FileSystemWatcher` fires twice on save** — 1-second debounce on any config reload
10. **`SetForceEnvironment()` not `m_forceEnv`** — the method triggers an immediate refresh; direct field assignment does not
11. **Harmony parameter names must match exactly** — always decompile to verify before writing a patch; don't guess
12. **Server guards** — all server logic must be inside `if (ZNet.instance == null || !ZNet.instance.IsServer()) return`
13. **`EnvMan` is client-local** — calling `SetForceEnvironment` on the server only affects the server process (no player there); use a custom RPC to call it on clients if you want weather effects

---

## Edge Cases to Handle

| Situation | Handling |
|---|---|
| Only 1 player — SwapEquipment scenario | Fall back to `GoNaked` |
| Player disconnects mid-event | On peer cleanup, remove from `AlivePeers`; if empty, call `End()` |
| Teleport position in solid terrain | Sample height and offset upward; retry up to 100 times before giving up |
| Player dies instantly (ocean, fall) before saving inventory | `SaveInventory` RPC must arrive and be processed before the teleport — add a short delay or await acknowledgement |
| Player was dead / offline when event started | Only include peers that are actually present in `AlivePeers` |
| Event ends but client never received `RestoreInventory` | Client restores on respawn if `IsEventActive` was true — add a safety restore in the respawn patch |

---

## Open Questions — Answer via Decompile, Not Guessing

1. **Is `EnvMan.SkipToMorning` the right hook?** Or is there a `Game.EveryoneSleeping` / `SleepStart` that fires earlier?
2. **`RPC_TeleportPlayer` exact signature** — is it `(Vector3, Quaternion, bool)` or different?
3. **Is there a built-in no-skill-loss flag?** Check `World`, `Player`, or `Game` for something like `m_noSkillGain` before patching `Skills.OnDeath`.
4. **`GetServerPeerID()`** — how does a client get the server peer UID to send RPCs back? Decompile `ZRoutedRpc` or `ZNet`.
5. **`Inventory.Save/Load` stability** — does it save equipped state too, or just items? Is it stable to call during an event?
6. **`SpawnObject` RPC signature** — confirm it is `(Vector3, Quaternion, int)` and that it works server-to-all for spawning creatures.
7. **Respawn location** — confirm that if spawn point is set to a bed, dying during the event will naturally respawn at that bed without any additional work.
8. **`EnvMan.SetForceEnvironment("")`** — does passing empty string clear the override, or does it throw / silently fail? Decompile `SetForceEnvironment` to check the empty-string path.
