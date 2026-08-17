# Game Pass (Microsoft Store) support

CardShopCoop 1.0.37 cannot host on the Xbox Game Pass / Microsoft Store build of
TCG Card Shop Simulator. The joiner connects, the roster and live sync work, but the
joiner never loads into the shop — they sit on the main menu while the host logs:

```
Could not snapshot the shop: Host snapshot save file missing after save
```

This document describes the root cause and the fix. Verified working on Game Pass
(Unity 6000.0.66, BepInEx 5.4.23.5) with two players in the same shop.

---

## Root cause

`SaveTransfer` assumes the game stores saves as plain files:

```
%USERPROFILE%\AppData\LocalLow\OPNeonGames\Card Shop Simulator\savedGames_Release<slot>.json
```

That is true on Steam. It is **not** true on Game Pass.

`Assembly-CSharp` contains two save backends, selected at runtime:

| | Steam | Game Pass |
|---|---|---|
| Backend | local JSON files | Xbox Game Save ("wgs") containers |
| Location | `persistentDataPath/savedGames_Release<N>.json` | `%LOCALAPPDATA%\Packages\<pkg>\SystemAppData\wgs\` |
| Container name | n/a | `GameData_<slot>` |

Relevant strings in `Assembly-CSharp.dll`:

```
/savedGames_Release0.json      GameData_
XGameSave initialized succesfully
GamecoreSaveManager  XGameSaveReadBlobDataAsync  SubmitGameSaveUpdate
```

So on Game Pass:

1. `CGameManager.SaveGameData(slot)` succeeds, but writes an Xbox container — **no local
   json is ever created**.
2. `BuildHostPayload`'s `File.Exists(path)` check therefore always fails.
3. `SendWorldTo` throws, the world is never sent, and the joiner waits forever.

The joiner side has the mirror-image problem: `ApplyAndLoad` writes
`savedGames_Release<CoopSlot>.json` and calls the load path, but the Game Pass build
never reads that file, so the load silently keeps the joiner's own world.

### Why reading the wgs container directly does not work

The obvious workaround — read the blob out of the wgs container — is a dead end.
`containers.index` (which maps `GameData_<slot>` to its folder GUID) is **not visible to
the game's own process**: MSIX filesystem redirection presents a filtered view. From
inside the game, `Directory.Exists(wgs)` is `true`, but the folder enumerates as only:

```
000901FEE2642BC4_00000000000000000000000064B0EFFE, t
```

with no `containers.index` at all, even though Explorer shows it plainly.
`CSaveLoad.GetLocalSaveFileAsByteArray()` exists (static, no parameters) but returns
null in this context.

---

## The fix

`CSaveLoad` holds the entire save in memory as a static field, and the game's own writer
is simply `JsonUtility.ToJson` of that object:

```csharp
// CSaveLoad
public static CGameData m_SavedGame = new CGameData();
private const string m_FileName = "/savedGames_Release";
private const string m_JSONFileNameExt = ".json";
```

So the contents of `savedGames_Release<N>.json` are exactly
`JsonUtility.ToJson(CSaveLoad.m_SavedGame)`. Serializing that object directly produces
byte-identical output **with no filesystem access**, which sidesteps the platform split
entirely.

**Host** (`BuildHostPayload`): call `SaveGameData` to flush the live world into
`m_SavedGame`, then serialize `m_SavedGame` and send that. Falls back to the original
file read if the field can't be reached.

**Joiner** (`ApplyAndLoad`): deserialize the received JSON straight back into
`m_SavedGame` before triggering the load. Still writes the slot file, since that is what
the Steam load path uses.

Both changes are additive — the Steam path is unchanged and still works.

---

## Secondary fix: build failure on the Game Pass assembly

`TournamentSync.cs` fails to **compile** against the Game Pass `Assembly-CSharp` because
`TournamentPrizeShelf.m_ScreenMesh` does not exist there:

```
error CS1061: 'TournamentPrizeShelf' does not contain a definition for 'm_ScreenMesh'
```

Resolved via cached reflection, so a missing or renamed field disables one cosmetic
show/hide rather than breaking the build.

---

## Files changed

| File | Change |
|---|---|
| `src/CardShopCoop/Sync/SaveTransfer.cs` | In-memory serialize/inject of `CSaveLoad.m_SavedGame` on both sides; file paths kept as fallback |
| `src/CardShopCoop/Sync/TournamentSync.cs` | `m_ScreenMesh` via reflection so the project compiles against builds lacking the field |

No changes are required to `CoopPlugin.cs`, `GamePatches.cs`, or the `.csproj` beyond the
per-machine `<GamePath>` every builder sets anyway.

---

## Building on Game Pass

Notes for anyone reproducing this, since Game Pass differs from the Steam instructions:

1. The game folder is `C:\XboxGames\<GUID>\Content` — note the **`Content`** suffix; set
   `<GamePath>` to that folder (the one containing `Card Shop Simulator_Data`).
2. `com.rlabrecque.steamworks.net.dll` is **not shipped** in the Game Pass
   `Managed` folder. The project references it, so drop in the standalone build from
   [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET/releases) under that exact
   filename to satisfy the compile-time reference. Steam co-op is unavailable at runtime on
   Game Pass regardless; use LAN / direct IP.
3. That standalone assembly targets .NET Framework 4.8, so set
   `<TargetFramework>net48</TargetFramework>` (the project ships `net472`, which cannot
   reference a 4.8 assembly).
4. Build the source **outside** the game directory. Building inside
   `C:\XboxGames\...\Content` is possible but confusing, and the `Packages` tree is
   permission-restricted.
5. Both players need the identical built `CardShopCoop.dll`.

## Testing done

- Host on Game Pass, joiner on Game Pass: joiner loads into the host's shop, shelves /
  stock / licenses / day and time all match. Verified with a day-52 save.
- Steam path unchanged (in-memory path is preferred there too, and produces the same
  bytes the file path produced).
