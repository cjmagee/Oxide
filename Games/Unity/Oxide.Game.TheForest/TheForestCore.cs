﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.TheForest.Libraries.Covalence;
using Steamworks;
using TheForest.UI;
using TheForest.Utils;
using UnityEngine;

namespace Oxide.Game.TheForest
{
    /// <summary>
    /// The core The Forest plugin
    /// </summary>
    public class TheForestCore : CSPlugin
    {
        #region Initialization

        // The permission library
        private readonly Permission permission = Interface.Oxide.GetLibrary<Permission>();
        private static readonly string[] DefaultGroups = { "default", "moderator", "admin" };

        // The covalence provider
        internal static readonly TheForestCovalenceProvider Covalence = TheForestCovalenceProvider.Instance;

        // TODO: Localization of core

        // Track when the server has been initialized
        private bool serverInitialized;
        private bool loggingInitialized;

        /// <summary>
        /// Initializes a new instance of the TheForestCore class
        /// </summary>
        public TheForestCore()
        {
            var assemblyVersion = TheForestExtension.AssemblyVersion;

            // Set attributes
            Name = "TheForestCore";
            Title = "The Forest";
            Author = "Oxide Team";
            Version = new VersionNumber(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);

            var plugins = Interface.Oxide.GetLibrary<Core.Libraries.Plugins>();
            if (plugins.Exists("unitycore")) InitializeLogging();
        }

        /// <summary>
        /// Starts the logging
        /// </summary>
        private void InitializeLogging()
        {
            loggingInitialized = true;
            CallHook("InitLogging", null);
        }

        /// <summary>
        /// Checks if the permission system has loaded, shows an error if it failed to load
        /// </summary>
        /// <returns></returns>
        private bool PermissionsLoaded(BoltEntity entity)
        {
            if (permission.IsLoaded) return true;
            // TODO: PermissionsNotLoaded reply to player
            return false;
        }

        #endregion

        #region Plugin Hooks

        /// <summary>
        /// Called when the plugin is initializing
        /// </summary>
        [HookMethod("Init")]
        private void Init()
        {
            // Configure remote logging
            RemoteLogger.SetTag("game", Title.ToLower());
            RemoteLogger.SetTag("game version", TheForestExtension.GameVersion);

            // Setup the default permission groups
            if (permission.IsLoaded)
            {
                var rank = 0;
                for (var i = DefaultGroups.Length - 1; i >= 0; i--)
                {
                    var defaultGroup = DefaultGroups[i];
                    if (!permission.GroupExists(defaultGroup)) permission.CreateGroup(defaultGroup, defaultGroup, rank++);
                }
                permission.RegisterValidate(s =>
                {
                    ulong temp;
                    if (!ulong.TryParse(s, out temp)) return false;
                    var digits = temp == 0 ? 1 : (int)Math.Floor(Math.Log10(temp) + 1);
                    return digits >= 17;
                });
                permission.CleanUp();
            }
        }

        /// <summary>
        /// Called when a plugin is loaded
        /// </summary>
        /// <param name="plugin"></param>
        [HookMethod("OnPluginLoaded")]
        private void OnPluginLoaded(Plugin plugin)
        {
            if (serverInitialized) plugin.CallHook("OnServerInitialized");
            if (!loggingInitialized && plugin.Name == "unitycore") InitializeLogging();
        }

        #endregion

        #region Server Hooks

        /// <summary>
        /// Called when the server is first initialized
        /// </summary>
        [HookMethod("OnServerInitialized")]
        private void OnServerInitialized()
        {
            if (serverInitialized) return;
            serverInitialized = true;

            Analytics.Collect();

            // Add some Steam tags
            SteamGameServer.SetGameTags("oxide,modded");

            // Update server console window and status bars
            TheForestExtension.ServerConsole();

            // Save the level every X minutes
            Interface.Oxide.GetLibrary<Timer>().Repeat(300f, 0, () =>
            {
                LevelSerializer.SaveGame("Game"); // TODO: Make optional
                LevelSerializer.Checkpoint();
                Interface.Oxide.LogInfo("Server has been saved!");
            });

            // Check if client should be disabled
            //if (commandLine.HasVariable("batchmode") || commandLine.HasVariable("nographics"))
            //{
            TheForestExtension.DisableAudio();
            TheForestExtension.DisableClient();
            //}
        }

        /// <summary>
        /// Called when the server is saving
        /// </summary>
        [HookMethod("OnServerSave")]
        private void OnServerSave() => Analytics.Collect();

        /// <summary>
        /// Called when the server is shutting down
        /// </summary>
        [HookMethod("IOnServerShutdown")]
        private void IOnServerShutdown()
        {
            Interface.Call("OnServerShutdown");
            Interface.Oxide.OnShutdown();
        }

        #endregion

        #region Player Hooks

        /// <summary>
        /// Called when a user is attempting to connect
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        [HookMethod("IOnUserApprove")]
        private object IOnUserApprove(BoltConnection connection)
        {
            var id = connection.RemoteEndPoint.SteamId.Id.ToString();
            var cSteamId = new CSteamID(connection.RemoteEndPoint.SteamId.Id);
            var name = SteamFriends.GetFriendPersonaName(cSteamId);

            // Get IP address from Steam
            P2PSessionState_t sessionState;
            SteamGameServerNetworking.GetP2PSessionState(cSteamId, out sessionState);
            var remoteIp = sessionState.m_nRemoteIP;
            var ip = string.Concat(remoteIp >> 24 & 255, ".", remoteIp >> 16 & 255, ".", remoteIp >> 8 & 255, ".", remoteIp & 255);

            // Call out and see if we should reject
            var canLogin = Interface.Call("CanClientLogin", connection) ?? Interface.Call("CanUserLogin", name, id, ip);
            if (canLogin is string || (canLogin is bool && !(bool)canLogin))
            {
                var coopKickToken = new CoopKickToken
                {
                    KickMessage = canLogin is string ? canLogin.ToString() : "Connection was rejected", // TODO: Localization
                    Banned = false
                };
                connection.Disconnect(coopKickToken);
                return true;
            }

            return Interface.Call("OnUserApprove", connection) ?? Interface.Call("OnUserApproved", name, id, ip);
        }

        /// <summary>
        /// Called when the player sends a message
        /// </summary>
        /// <param name="evt"></param>
        [HookMethod("OnPlayerChat")]
        private object OnPlayerChat(ChatEvent evt)
        {
            var entity = Scene.SceneTracker.allPlayerEntities.FirstOrDefault(ent => ent.networkId == evt.Sender);
            if (entity == null) return null;

            var id = entity.source.RemoteEndPoint.SteamId.Id;
            var name = entity.GetState<IPlayerState>().name;

            Debug.Log($"[Chat] {name}: {evt.Message}");

            // Call covalence hook
            var iplayer = Covalence.PlayerManager.FindPlayerById(id.ToString());
            return iplayer != null ? Interface.Call("OnUserChat", iplayer, evt.Message) : null;
        }

        /// <summary>
        /// Called when the player has connected
        /// </summary>
        /// <param name="entity"></param>
        [HookMethod("OnPlayerConnected")]
        private void OnPlayerConnected(BoltEntity entity)
        {
            var id = entity.source.RemoteEndPoint.SteamId.Id.ToString();
            var name = SteamFriends.GetFriendPersonaName(new CSteamID(entity.source.RemoteEndPoint.SteamId.Id));

            Debug.Log($"{id}/{name} joined");

            // Do permission stuff
            if (permission.IsLoaded)
            {
                // Update stored name
                permission.UpdateNickname(id, name);

                // Add player to default group
                if (!permission.UserHasGroup(id, DefaultGroups[0])) permission.AddUserGroup(id, DefaultGroups[0]);
            }

            // Let covalence know
            Covalence.PlayerManager.NotifyPlayerConnect(entity);
            var iplayer = Covalence.PlayerManager.FindPlayerById(id);
            if (iplayer != null) Interface.Call("OnUserConnected", iplayer);
        }

        /// <summary>
        /// Called when the player has disconnected
        /// </summary>
        /// <param name="connection"></param>
        [HookMethod("IOnPlayerDisconnected")]
        private void IOnPlayerDisconnected(BoltConnection connection)
        {
            var id = connection.RemoteEndPoint.SteamId.Id;
            var entity = Scene.SceneTracker.allPlayerEntities.FirstOrDefault(ent => ent.source.RemoteEndPoint.SteamId.Id == id);
            if (entity == null) return;

            var name = entity.GetState<IPlayerState>().name;

            Debug.Log($"{id}/{name} quit");

            // Call game hook
            Interface.Call("OnPlayerDisconnected", entity);

            // Let covalence know
            var iplayer = Covalence.PlayerManager.FindPlayerById(id.ToString());
            if (iplayer != null) Interface.Call("OnUserDisconnected", iplayer, "Unknown");
            Covalence.PlayerManager.NotifyPlayerDisconnect(entity);
        }

        /// <summary>
        /// Called when the player spawns
        /// </summary>
        /// <param name="entity"></param>
        [HookMethod("OnPlayerSpawn")]
        private void OnPlayerSpawn(BoltEntity entity)
        {
            // Call covalence hook
            var iplayer = Covalence.PlayerManager.FindPlayerById(entity.source.RemoteEndPoint.SteamId.Id.ToString());
            if (iplayer != null) Interface.Call("OnUserSpawn", iplayer);
        }

        #endregion

        #region Server Magic

        /// <summary>
        /// Initializes the server
        /// </summary>
        [HookMethod("InitServer")]
        private void InitServer()
        {
            VirtualCursor.Instance.enabled = false;

            Interface.Oxide.NextTick(() =>
            {
                var coop = UnityEngine.Object.FindObjectOfType<TitleScreen>();
                coop.OnCoOp();
                coop.OnMpHost();

                // Check for saved games
                if (LevelSerializer.SavedGames.Count > 0)
                {
                    coop.OnLoad();
                    coop.OnSlotSelection((int)GameSetup.Slot);
                }
                else
                {
                    coop.OnNewGame(GameSetup.DifficultyModes.Normal); // TODO: Difficulty mode command-line option
                }
            });
        }

        /// <summary>
        /// Sets up the coop lobby
        /// </summary>
        [HookMethod("ILobbySetup")]
        private void ILobbySetup(Enum screen)
        {
            var type = typeof(CoopSteamNGUI).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(x => x.IsEnum && x.Name.Equals("Screens"));
            var enumValue = type?.GetField("LobbySetup", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            if (enumValue != null) if (Convert.ToInt32(screen) != (int)enumValue) return;

            Interface.Oxide.NextTick(() =>
            {
                var coop = UnityEngine.Object.FindObjectOfType<CoopSteamNGUI>();
                coop.OnHostLobbySetup();
            });
        }

        /// <summary>
        /// Starts the server from lobby screen
        /// </summary>
        [HookMethod("ILobbyReady")]
        private void ILobbyReady()
        {
            Interface.Oxide.NextTick(() =>
            {
                var coop = UnityEngine.Object.FindObjectOfType<CoopSteamNGUI>();
                coop.OnHostStartGame();
            });
        }

        /// <summary>
        /// Skips the plane cut scene
        /// </summary>
        [HookMethod("ICutScene")]
        private void ICutScene(TriggerCutScene scene)
        {
            Scene.TriggerCutScene.SpaceTut.SetActive(false);
            Scene.TriggerCutScene.LightsFlight.SetActive(false);
            Scene.TriggerCutScene.pmTrigger.SendEvent("toSkipOpening");
            //Scene.TriggerCutScene.skipOpening = true;
            scene.planeController.setPlanePosition();

            if (Scene.TriggerCutScene && Scene.TriggerCutScene.planeController) Scene.TriggerCutScene.planeController.enabled = false;
            var transform = PlaneCrashLocations.finalPositions[PlaneCrashLocations.crashSite].transform;
            scene.planeController.transform.position = transform.position;
            scene.planeController.transform.rotation = transform.rotation;
            var gameObjectArray = scene.enableAfterCrash;
            foreach (var gameObject in gameObjectArray) if (gameObject) gameObject.SetActive(true);
            Scene.PlaneGreebles.transform.position = scene.planeController.transform.position;
            Scene.PlaneGreebles.transform.rotation = scene.planeController.transform.rotation;
            Scene.PlaneGreebles.SetActive(true);

            UnityEngine.Object.Destroy(scene);
        }

        /// <summary>
        /// Overrides the default save path
        /// </summary>
        /// <returns></returns>
        [HookMethod("IGetSavePath")]
        private string IGetSavePath()
        {
            var saveDir = Path.Combine(Interface.Oxide.RootDirectory, "saves/");
            if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
            return saveDir;
        }

        #endregion
    }
}
