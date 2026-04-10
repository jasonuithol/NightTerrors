# NightTerrors — Lessons Learned

Wisdom extracted from actually building and testing this mod. Complements CLAUDE.md,
which covers design intent. This covers what we discovered when theory met reality.

---

## Build & Deploy

### Use `$(HOME)` in the csproj, not a hardcoded path
```xml
<ValheimDir>$(HOME)/.steam/steam/steamapps/common/Valheim dedicated server</ValheimDir>
```
Hardcoding `/home/<this guys username>/...` leaks PII into a public repo. MSBuild expands `$(HOME)`
correctly at build time. The `obj/` files still contain the resolved path, but they are
gitignored and never committed.

### `obj/` is safe to gitignore — the DLL has no source paths
The generated `obj/project.assets.json` contains resolved home-directory paths, but
none of that survives into the compiled DLL. End users on Thunderstore see no PII.

### Deploy scripts run from the host — use container-relative paths carefully
The MCP `deploy_server` / `deploy_client` tools run on the **host**.
The tools reference `bin/Release/netstandard2.1/NightTerrors.dll` (relative) which
resolves correctly on the host. But the deploy target (`/workspace/valheim/...`) is the
container's bind-mounted workspace — the host can write there directly.

### Steam must be running before starting the client
`mcp__valheim__start_client` will fail silently if Steam is not already running on the
host. Always confirm Steam is up first.

### Server IP changes on every restart
Don't hardcode or remember the server IP between sessions. After every server restart,
check the log:
```
grep "IPv4" /workspace/valheim/server/BepInEx/LogOutput.log | tail -1
```

---

## Harmony Patching

### Private methods must be patched by string name
Harmony resolves overloads by parameter types. For private methods you can't reference
with `nameof()`, pass the name as a string:
```csharp
[HarmonyPatch(typeof(EnvMan), "OnMorning")]         // private
[HarmonyPatch(typeof(SleepText), "ShowDreamText")]  // private
[HarmonyPatch(typeof(Player), "AddKnownBiome")]     // private
```
Always decompile first to confirm the exact name and that it has no parameters to match.

### `ZRoutedRpc` has no `Awake()` — patch the constructor
`ZRoutedRpc` is a plain class, not a MonoBehaviour. It has no `Awake`. Patch the
constructor instead:
```csharp
[HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, new[] { typeof(bool) })]
```
This fires on both server and client when `ZRoutedRpc` initialises.

### Always decompile before writing a patch — never guess parameter names
Harmony matches parameters by name. A wrong name silently fails to inject.
`nameof()` won't compile for protected/private members — another reason to decompile first.

### `Prefix` returning `false` skips the original — use it for suppression
Returning `false` from a Prefix completely skips the original method. This is how
skill loss, tombstone creation, item discovery, biome discovery, dream text, and the
"good morning" message are all suppressed during the event.

---

## RPC Architecture

### Register ALL RPCs on both server and client — guard inside the handler
Register every RPC in the `ZRoutedRpc` constructor patch, regardless of which side
handles it. Use guards inside:
```csharp
if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // server-only handler
if (Player.m_localPlayer == null) return;                        // client-only handler
```

### RPC ordering within a connection is guaranteed
RPCs sent to the same peer arrive in the order they were sent. This means sending
`RestoreInventory` then `KillSurvivor` then `EventEnd` to a client will be processed
in that exact order — inventory is restored, then they die (with suppression still active),
then the event flag clears.

### Client → Server RPC: use `InvokeRoutedRPC` with no target (goes to server automatically)
```csharp
ZRoutedRpc.instance.InvokeRoutedRPC("NightTerrors_PlayerDied");
// No peer ID needed — unaddressed RPCs route to the server.
```

### The server cannot access client inventory directly — use upload RPCs
For the SwapEquipment scenario, the server needs inventory bytes from each client.
Clients must send their bytes up via a Client→Server RPC after saving locally.
A fixed 2.2s wait is enough for all clients to upload before the server proceeds.

---

## Event Lifecycle

### `IsEventStarting` must be set before `IsEventActive`
The `SaveInventory` RPC arrives before `EventStart`. Setting `IsEventStarting = true`
on receipt allows patches (dream text, "good morning") to suppress correctly during
the 2.2s window before the event is fully underway.

### Survivors need an explicit kill RPC — `End()` alone doesn't send them home
When the event timer expires, `End()` restores inventories and clears the event flag,
but alive players are still standing at the teleport location. Without a `KillSurvivor`
RPC they can roam indefinitely. The RPC must be sent **before** `EventEnd` so that
tombstone and skill-loss suppression patches are still active when they die.

### Send `KillSurvivor` only to `AlivePeers`, not `Everybody`
Players who already died naturally are no longer in `AlivePeers`. Sending to `Everybody`
would re-kill them at their respawn point. Iterate `AlivePeers` explicitly.

### Apply massive damage, don't call `OnDeath()` directly
`SetHealth(0f)` does not trigger death on its own. Go through the normal damage path:
```csharp
var hit = new HitData();
hit.m_damage.m_blunt = 99999f;
hit.m_point = player.transform.position;
player.Damage(hit);
```
This fires `CheckDeath()` → `OnDeath()` → all the right hooks.

---

## Inventory Management

### Save inventory as `ZPackage` bytes, restore by loading those bytes
```csharp
// Save
var pkg = new ZPackage();
player.GetInventory().Save(pkg);
byte[] bytes = pkg.GetArray();

// Restore
player.GetInventory().Load(new ZPackage(bytes));
```
`GetInventory()` is the public accessor for the protected `m_inventory` field.
`Save/Load` serialises the full inventory including item counts, quality, and crafting data.

### Always `UnequipAllItems()` before loading a new inventory
Loading inventory bytes while items are equipped can leave ghost equipment state.
Call `player.UnequipAllItems()` first, then load.

### Suppress `AddKnownItem` and `AddKnownBiome` during the event
When items are granted or a new biome is discovered during the dream:
- `Player.AddKnownItem()` — updates `m_knownMaterial`, shows "new item!" unlock popup
- `Player.AddKnownBiome()` — updates `m_knownBiome`, shows biome discovery message

Both should be suppressed during the event so players encounter these things fresh in
real gameplay. Patch both as Prefix returning `false` when `IsEventActive || IsEventStarting`.

---

## Messages & UI

### Use `ShowMessage` RPC for all player-facing text — not `ChatMessage`
`ChatMessage` requires a real platform ID and throws `EndOfStreamException` with a
fake one. `ShowMessage` is safe for mods:
```csharp
ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)type, text);
```

### "Day X" fires via `EnvMan.OnMorning` — suppress it, deliver it on respawn
`EnvMan.OnMorning` runs when the day transitions. During the event this fires while
players are at the dream location. Suppress it with a `SuppressMorning` flag, then
deliver the message in a `Player.OnSpawned` postfix when they respawn at their bed:
```csharp
__instance.Message(MessageHud.MessageType.Center, $"Day {EnvMan.instance.GetDay()}");
```
Using `EnvMan.instance.GetDay()` directly avoids the `Localization` class, which is
cleaner for a mod anyway.

### "Good morning" and dream text need separate suppression
- `$msg_goodmorning` goes through `Player.Message()` — patch that
- Dream text comes from `SleepText.ShowDreamText()` (private) — patch by name
Both need to be suppressed as soon as `IsEventStarting` is true (i.e. when `SaveInventory`
RPC arrives), not just when `IsEventActive` is set.

---

## Weather

### `EnvMan` is client-local — you cannot drive weather from the server
`EnvMan.instance.SetForceEnvironment()` on the server affects only the server process.
No players are there. Send an RPC and call it on each client instead.

### `SetForceEnvironment("")` clears the override cleanly
Passing an empty string to `SetForceEnvironment` restores natural weather without
throwing. Call this in `End()` to clean up.

### Use `SetForceEnvironment()`, not direct field assignment
The method triggers an immediate environment refresh. Direct `m_forceEnv` assignment
does not. Always use the method.

---

## Teleportation

### Distant teleport causes a black loading screen — this is normal
`RPC_TeleportPlayer` with `distantTeleport = true` produces a black screen while the
destination zone loads. This is expected engine behaviour, not a bug. Players can hear
audio and see inventory changes during this window.

### The world is 20,000 × 20,000 — random positions cover the full range
`WorldGenerator.GetHeight(x, z)` and `WorldGenerator.GetBiome(x, z)` work anywhere
in `[-10000, 10000]`. Retry up to 100 times to find land; in practice it finds one
in the first few attempts.

---

## Jumpscare (Removed)

### The Wraith jumpscare was cut — two reasons
1. **Camera angle dependency**: the effect only works if the Wraith charges directly
   at the camera. At wrong angles it's confusing or invisible.
2. **Client ownership issue**: the server spawns the Wraith, but a nearby client claims
   ZDO ownership. Server-side `ZDOMan.DestroyZDO()` calls don't propagate reliably when
   the client owns it, leaving a live Wraith roaming the world killing pigs indefinitely.

The jumpscare concept is appealing but requires client-side spawning or a more careful
ownership handoff. Shelved for now.

---

## Monster Spawning

### `SpawnObject` RPC works for spawning creatures server-to-all
```csharp
ZRoutedRpc.instance.InvokeRoutedRPC(
    ZRoutedRpc.Everybody, "SpawnObject",
    spawnPos, Quaternion.identity, prefabNameHash);
```
Use `name.GetStableHashCode()` for the hash. This is fire-and-forget — no handle
returned, no way to despawn later. Fine for event monsters; not fine for things you
need to clean up.

### Biome detection uses `WorldGenerator.GetBiome(x, z)` — check ocean separately
`GetBiome` returns the terrain biome, but ocean is detected by comparing
`WorldGenerator.GetHeight(x, z)` against `ZoneSystem.instance.m_waterLevel`, not by
biome enum. Check height first, then biome.

---

## Packaging

### Thunderstore zip structure
The package tool expects:
```
ThunderstoreAssets/
  manifest.json   ← name, version, description, dependencies, website_url
  README.md       ← shown on Thunderstore mod page
  icon.png        ← exactly 256×256 PNG
```
The DLL is staged into `plugins/NightTerrors.dll` inside the zip automatically.

### Icon pipeline: SVG → PNG via `rsvg-convert`
Author the icon as an SVG (version-controlled, editable), convert to 256×256 PNG with
the `mcp__valheim__convert_svg` tool. Only commit the SVG; the PNG is a build artifact.

---

## General Valheim Modding Gotchas

| Gotcha | Detail |
|--------|--------|
| Ghost peers | Always `if (peer.m_uid == 0) continue` when iterating `ZNet.instance.GetPeers()` |
| `ChatMessage` | Needs a real platform ID — throws with fake ones. Use `ShowMessage` instead |
| Equipment ZDO values | Stored as int hashes: `zdo.GetInt(ZDOVars.s_rightItem)` — NOT strings |
| Emote ZDO | Exception: emote IS a string — `zdo.GetString(ZDOVars.s_emote)` |
| `BroadcastMessage` clash | Exists on all `MonoBehaviour`; don't name a method `BroadcastMessage` |
| `mkdir -p` in deploy scripts | The `plugins/` directory may not exist on first deploy |
| `StopEmote` fires every frame | Debounce with a flag if you ever patch it |
| `FileSystemWatcher` double-fire | 1-second debounce on any config reload watcher |
| `SetForceEnvironment` vs `m_forceEnv` | Always use the method — direct field doesn't refresh |
| `netstandard2.1` not `net462` | On Linux the `net462` target fails to resolve refs correctly |
| BepInEx config is not hot-reloaded | Server must be restarted to pick up `.cfg` changes |
