using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using Utilla.Attributes;

namespace GorillaMerge
{
    /// <summary>
    /// GorillaMerge: puts other games INTO Gorilla Tag.
    ///
    /// Each game is a "pack" (Packs/&lt;Name&gt;/pack.json). A voxel pack gives you that game's blocks:
    /// build and break them anywhere with your gorilla hands, and travel to a generated world made of them.
    /// A map pack loads a 3D level (.obj) as a world you can climb with Gorilla Tag movement.
    /// Minecraft is the first pack; its textures and sounds are read at runtime from your own
    /// Minecraft Java install, nothing from Mojang is shipped.
    ///
    /// Everything is local to your own game. It only runs when you are not in a room, or in a
    /// modded lobby, because solid blocks you can climb would be an unfair advantage in public lobbies.
    /// </summary>
    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    [BepInDependency("org.legoandmars.gorillatag.utilla", BepInDependency.DependencyFlags.HardDependency)]
    [ModdedGamemode]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public ConfigEntry<bool> Enabled;
        public ConfigEntry<float> BlockSize;
        public ConfigEntry<float> Reach;
        public ConfigEntry<float> AirPlaceDistance;
        public ConfigEntry<bool> PunchToBreak;
        public ConfigEntry<float> PunchSpeed;
        public ConfigEntry<bool> SwapHands;
        public ConfigEntry<float> AimPitch;
        public ConfigEntry<float> UiScale;
        public ConfigEntry<bool> Music;
        public ConfigEntry<float> SoundVolume;
        public ConfigEntry<string> MinecraftVersion;
        public ConfigEntry<string> BuildPack;
        public ConfigEntry<int> WorldSize;
        public ConfigEntry<int> WorldSeed;
        public ConfigEntry<float> WorldHeightOffset;
        public ConfigEntry<bool> ShowWristInPublic;
        public ConfigEntry<bool> AutoTest;
        public ConfigEntry<bool> PhotoShots;
        public ConfigEntry<string> GameMode;
        public ConfigEntry<int> RenderDistance;
        public ConfigEntry<float> DayLength;
        public ConfigEntry<float> HudScale;
        public ConfigEntry<float> HudAngle;
        public ConfigEntry<bool> Mobs;
        public ConfigEntry<int> MobCap;
        public ConfigEntry<float> PunchPower;

        /// <summary>True while we are inside a Utilla modded room.</summary>
        public bool InModdedRoom { get; private set; }

        private GameObject _managerObject;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Master switch for the mod.");
            BuildPack = Config.Bind("General", "BuildPack", "Minecraft",
                "Which voxel pack's blocks you build with in Gorilla Tag's own maps.");
            MinecraftVersion = Config.Bind("General", "MinecraftVersion", "",
                "Minecraft version to read textures and sounds from (a folder name in .minecraft/versions). Empty = newest release you have installed.");
            ShowWristInPublic = Config.Bind("General", "ShowWristInPublic", true,
                "In public lobbies the mod is off. Show a small wrist label (with a Go Offline button) so you know why.");

            BlockSize = Config.Bind("Building", "BlockSize", 0.5f,
                "Size of one block in meters. A Gorilla Tag gorilla is roughly 2.5 blocks tall at 0.5.");
            Reach = Config.Bind("Building", "Reach", 2.5f, "How far your hands can reach blocks, in meters.");
            AirPlaceDistance = Config.Bind("Building", "AirPlaceDistance", 0.35f,
                "When you aren't pointing at a block, a new block appears this far in front of your hand.");
            PunchToBreak = Config.Bind("Building", "PunchToBreak", true,
                "Make a fist (hold grip) and punch a block to break it.");
            PunchSpeed = Config.Bind("Building", "PunchSpeed", 1.2f, "How fast (m/s) a fist has to hit to count as a punch.");
            SwapHands = Config.Bind("Building", "SwapHands", false,
                "Normally the RIGHT trigger places and the LEFT trigger mines. Set true to swap.");
            AimPitch = Config.Bind("Building", "AimPitch", 0f,
                "Tilts where your hands point, in degrees (negative = up). Change it if blocks land above or below where you aim.");
            UiScale = Config.Bind("Building", "WristScale", 1f, "Size of the block bar and menu above your left hand.");

            Music = Config.Bind("Sound", "Music", true, "Play the game's music now and then while you are in a pack world.");
            SoundVolume = Config.Bind("Sound", "Volume", 0.8f, "Volume of block and world sounds (0-1).");

            WorldSize = Config.Bind("World", "Size", 128, "(Old island worlds only; worlds are endless now.)");
            GameMode = Config.Bind("World", "GameMode", "Survival", "Survival or Creative, for new pack worlds. You can switch in the wrist menu.");
            RenderDistance = Config.Bind("World", "RenderDistance", 6, "How many chunks (16 blocks each) you can see in every direction. Lower it if the game stutters.");
            DayLength = Config.Bind("World", "DayLength", 20f, "Minutes for a full day and night, like Minecraft's 20.");
            Mobs = Config.Bind("World", "Mobs", true, "Zombies and creepers at night and in caves, pigs in the day (survival).");
            MobCap = Config.Bind("World", "MobCap", 8, "Most hostile mobs around you at once.");
            HudScale = Config.Bind("Building", "HudScale", 1.3f, "Size of the hotbar, hearts and hunger at the bottom of your view.");
            HudAngle = Config.Bind("Building", "HudAngle", 24f, "How far below the middle of your view the hotbar sits, in degrees.");
            PunchPower = Config.Bind("Building", "PunchPower", 0.5f, "Seconds of mining each fist punch is worth (Minecraft logs take 3 s by hand).");
            WorldSeed = Config.Bind("World", "Seed", 20261102, "Seed for generated worlds. Change it (and delete the save) for a new world.");
            WorldHeightOffset = Config.Bind("World", "HeightOffset", 800f,
                "How high above Gorilla Tag's maps (meters) the pack worlds float.");

            AutoTest = Config.Bind("Debug", "AutoTest", false,
                "Developer self-test: goes offline, visits the worlds and saves screenshots. Leave off.");
            PhotoShots = Config.Bind("Debug", "PhotoShots", false,
                "Developer: builds a scene in a throwaway world and saves 4K pictures for the mod's page into config\\GorillaMerge\\photos, then turns itself off.");

            Log.LogInfo($"{ModInfo.Name} v{ModInfo.Version} loaded.");
            // Utilla 1.6.27 crashes while loading on the Oct 2026 game update, so its GameInitialized
            // event never fires. Start on our own instead; the manager waits until the player exists.
            StartManager();
            PunchGuard.Install(new HarmonyLib.Harmony(ModInfo.Guid));
            try { Utilla.Events.RoomLeft += OnAnyRoomLeft; } catch { }
        }

        /// <summary>Building is allowed offline or in modded lobbies, never in public ones.</summary>
        public static bool Allowed
        {
            get
            {
                if (Instance == null || !Instance.Enabled.Value) return false;
                try
                {
                    var net = NetworkSystem.Instance;
                    if (net == null || !net.InRoom) return true;
                    // Our own check too, in case Utilla is broken: modded lobbies have "MODDED" in their game mode.
                    string mode = net.GameModeString ?? "";
                    return Instance.InModdedRoom || mode.IndexOf("MODDED", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }
        }

        public static bool InRoom
        {
            get { try { return NetworkSystem.Instance != null && NetworkSystem.Instance.InRoom; } catch { return false; } }
        }

        private void StartManager()
        {
            try
            {
                if (_managerObject != null) return;
                _managerObject = new GameObject("GorillaMerge");
                DontDestroyOnLoad(_managerObject);
                _managerObject.AddComponent<MergeManager>();
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to start: " + ex);
            }
        }

        [ModdedGamemodeJoin]
        public void OnModdedJoin(string gamemode)
        {
            InModdedRoom = true;
            Log.LogInfo("Joined modded room (" + gamemode + ").");
        }

        [ModdedGamemodeLeave]
        public void OnModdedLeave(string gamemode)
        {
            InModdedRoom = false;
            Log.LogInfo("Left modded room (" + gamemode + ").");
        }

        private void OnAnyRoomLeft(object sender, Utilla.Events.RoomJoinedArgs e)
        {
            InModdedRoom = false;
        }

        private void OnDestroy()
        {
            try { Utilla.Events.RoomLeft -= OnAnyRoomLeft; } catch { }
        }
    }

    public static class ModInfo
    {
        public const string Guid = "dev.gorillamerge.gorillatag";
        public const string Name = "GorillaMerge";
        public const string Version = "1.0.1";
    }
}
