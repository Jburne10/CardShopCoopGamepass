using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CardShopCoop.Sync
{
    /// <summary>
    /// Join-time world sync: the host serializes its current shop with the game's own
    /// save pipeline and streams the JSON save file to the client. The client writes it
    /// into a dedicated slot (7) that the in-game save UI never shows, then boots it
    /// through the game's normal load path. Result: both players stand in the same shop,
    /// with the same shelves, stock, licenses and PTCGO expansions, without the client's
    /// own saves (slots 0-3) ever being touched.
    /// </summary>
    public static class SaveTransfer
    {
        /// <summary>Slot the co-op world lives in on the client (config: ClientWorldSlot).</summary>
        public static int CoopSlot => CoopPlugin.ClientWorldSlot?.Value ?? 7;

        /// <summary>Throwaway slot the HOST snapshots its live world into at every join
        /// (mirror of the client's coop slot 7 convention). We must NOT force-save over the
        /// host's REAL slot: SaveGameData writes the on-disk file immediately, so snapshotting
        /// into the live slot baked whatever the world looked like at that instant - including
        /// a transient mid-join shelf-stock wipe (see WorldSync FIX D-a) - permanently into the
        /// host's real save. Slot 6 is outside the vanilla range (0 = autosave, 1-3 = manual),
        /// the in-game save UI never shows it, and the host never LOADS it, so it's safe to
        /// clobber on every join.</summary>
        public const int HostSnapshotSlot = 6;

        public static string SlotPath(int slot)
        {
            return Application.persistentDataPath + "/savedGames_Release" + slot + ".json";
        }

        /// <summary>Reads the game's in-memory save object (CSaveLoad.m_SavedGame) and returns
        /// it serialized exactly the way the game serializes it to disk. Null if unavailable.
        ///
        /// GAME PASS FIX: on the Microsoft Store / Game Pass build the game does NOT write
        /// savedGames_Release&lt;slot&gt;.json at all - saves go to Xbox Game Save ("wgs") containers
        /// named GameData_&lt;slot&gt;, and MSIX redirection hides those from the game's own process,
        /// so no amount of file probing can find them. But the game keeps the entire save in
        /// memory as the static CSaveLoad.m_SavedGame, and its own writer is just
        /// JsonUtility.ToJson(m_SavedGame). Serializing that object ourselves therefore yields
        /// byte-identical content to the file the joiner expects, with no filesystem involved -
        /// which works on every platform.</summary>
        private static byte[] SerializeInMemorySave()
        {
            try
            {
                var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fld == null) return null;
                object saved = fld.GetValue(null);
                if (saved == null) return null;

                string json = JsonUtility.ToJson(saved);
                if (string.IsNullOrEmpty(json) || json.Length < 32 || json == "{}") return null;
                return new UTF8Encoding(false).GetBytes(json);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("coop: in-memory save serialization failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Host: flush the live game and return the save bytes.
        ///
        /// Preferred path is the in-memory one (see SerializeInMemorySave) because it is
        /// platform-independent. We still call SaveGameData first so the snapshot reflects the
        /// CURRENT world rather than the last manual save. The original file-based path is kept
        /// as a fallback for builds where the in-memory object can't be reached.</summary>
        public static byte[] BuildHostPayload()
        {
            var gm = CSingleton<CGameManager>.Instance;

            // Flush the live world into the game's in-memory save object. On Steam this also
            // writes savedGames_Release6.json; on Game Pass it writes an Xbox container. Either
            // way m_SavedGame ends up holding the current shop, which is all we need.
            int prevSlot = gm.m_CurrentSaveLoadSlotSelectedIndex;
            try
            {
                gm.SaveGameData(HostSnapshotSlot);
            }
            catch (Exception e)
            {
                // Some builds throw inside SaveGameData's save-slot UI refresh for this
                // out-of-band slot (the UI list has no button for slot 6). The in-memory
                // state is still updated, so keep going and let the check below decide.
                CoopPlugin.Log.LogWarning("coop: SaveGameData(" + HostSnapshotSlot + ") threw: " + e.Message);
            }
            finally
            {
                gm.m_CurrentSaveLoadSlotSelectedIndex = prevSlot; // undo SaveGameData's side effect
            }

            byte[] mem = SerializeInMemorySave();
            if (mem != null)
            {
                CoopPlugin.Log.LogInfo($"coop: host snapshot from CSaveLoad.m_SavedGame ({mem.Length / 1024} KB)");
                return mem;
            }

            // ---- fallback: original file-based path (Steam layout) ----
            string path = SlotPath(HostSnapshotSlot);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "Host snapshot save file missing after save, and the in-memory save object " +
                    "could not be read. (On Game Pass the game writes Xbox wgs containers rather " +
                    "than local json, so the in-memory path is required.)", path);
            byte[] bytes = File.ReadAllBytes(path);
            CoopPlugin.Log.LogInfo($"coop: host snapshot from slot file ({bytes.Length / 1024} KB)");
            return bytes;
        }

        /// <summary>Client: apply the received world and load it.
        ///
        /// GAME PASS FIX: writing savedGames_Release&lt;CoopSlot&gt;.json is not enough on the
        /// Microsoft Store build - the game never reads that file there (its saves live in Xbox
        /// wgs containers), so the subsequent load silently kept the client's own world. We
        /// therefore also deserialize the received JSON straight into CSaveLoad.m_SavedGame,
        /// which is the object the load path actually reads. The file write is retained because
        /// it is what the Steam load path uses.</summary>
        public static void ApplyAndLoad(byte[] saveBytes)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_ForceNoCloudSaveLoad = true; // keep cloud sync away from the borrowed world

            bool injected = false;
            try
            {
                string json = new UTF8Encoding(false).GetString(saveBytes)
                                                     .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (json.StartsWith("{", StringComparison.Ordinal))
                {
                    var fld = typeof(CSaveLoad).GetField("m_SavedGame",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fld != null)
                    {
                        object world = JsonUtility.FromJson(json, fld.FieldType);
                        if (world != null) { fld.SetValue(null, world); injected = true; }
                        else CoopPlugin.Log.LogWarning("coop: FromJson returned null for the received world");
                    }
                }
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("coop: injecting received world failed: " + e.Message); }

            try
            {
                string jsonPath = SlotPath(CoopSlot);
                string gdPath = Application.persistentDataPath + "/savedGames_Release" + CoopSlot + ".gd";
                File.WriteAllBytes(jsonPath, saveBytes);
                if (File.Exists(gdPath)) File.Delete(gdPath); // never fall back to a stale binary copy
            }
            catch (Exception e) { CoopPlugin.Log.LogWarning("coop: could not write co-op slot file: " + e.Message); }

            CoopPlugin.Log.LogInfo($"Coop save received ({saveBytes.Length / 1024} KB){(injected ? " [in-memory]" : "")}, loading world...");
            ForceLoadSlot(CoopSlot);
        }

        /// <summary>Drive the game's own title->shop load path for an arbitrary slot.</summary>
        public static void ForceLoadSlot(int slot)
        {
            var gm = CSingleton<CGameManager>.Instance;
            gm.m_CurrentSaveLoadSlotSelectedIndex = slot;

            // The load-on-scene-enter path only runs while m_InitLoaded is false.
            var initLoaded = typeof(CGameManager).GetField("m_InitLoaded",
                BindingFlags.NonPublic | BindingFlags.Static);
            initLoaded?.SetValue(null, false);

            gm.LoadMainLevelAsync("Start", slot);
        }
    }
}
