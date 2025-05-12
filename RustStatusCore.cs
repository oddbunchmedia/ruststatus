using System;
using System.Net;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;

using Network;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using UnityEngine;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;


//
//	This plugin is designed to work with the Rust Status APIs - https://ruststatus.com
//


namespace Oxide.Plugins {

	[Info("Rust Status", "ruststatus.com", "0.2.5")]
	[Description("The plugin component of the Rust Status platform.")]

	class RustStatusCore : RustPlugin {

		private ConfigData configData;

		string hostname = "https://api.ruststatus.com";
		string version = "v1";

		string serverGroupSecretKey;
		string serverSecretKey;

		bool useCentralisedBans;
		bool doHourlyBroadcast;
		bool debug;

		bool restartServerOnLowFramerate;
		int framerateMinimumPercentage;

		uint serverProtocol;

		bool discordWebhookServerWipesIsSet = false;
		bool discordWebhookServerStatusIsSet = false;
		bool discordWebhookPlayerBanStatusIsSet = false;

		bool suppressProtocolMismatchMessages = false;

		bool canSendToRustStatus = false;

		int playerCountHigh;
		int playerCountLow;
		int playerCountRangeLastSent;

		int playerCount = 0;

		int framerateHigh;
		int framerateLow;
		int framerateRangeLastSent;

		int fps = 0;

		bool restartInitiated = false;

		int framerateLowCount = 0;
		int framerateMaximum = 0;
		int framerateMinimum = 0;

		private readonly Dictionary<string, string> header = new Dictionary<string, string> {
			["Content-Type"] = "application/json"
		};

		void Init() {

			LoadConfigVariables();

			serverGroupSecretKey = configData.Keys.serverGroupSecretKey;
			serverSecretKey = configData.Keys.serverSecretKey;
			
			useCentralisedBans = configData.Services.useCentralisedBans;
			doHourlyBroadcast = configData.Services.doHourlyBroadcast;
			debug = configData.Services.debug;
			
			restartServerOnLowFramerate = configData.Options.restartServerOnLowFramerate;
			framerateMinimumPercentage = configData.Options.framerateMinimumPercentage;
			
			VerifyKeys();
			
			InitialiseServer();

		}

		void OnServerInitialized(bool initial) {

			serverProtocol = Rust.Protocol.network;

			int hourAgo = GetTimestamp() - 3600;


			// Player count range

			playerCountHigh = configData.Store.playerCountHigh;

			if (initial == true) {
				playerCountLow = 0;
			} else {
				playerCountLow = configData.Store.playerCountLow;
			}

			playerCountRangeLastSent = configData.Store.playerCountRangeLastSent;

			if (playerCountRangeLastSent < hourAgo) {
				SendHourlyPlayerCountRange();
			}


			// Server framerate range

			framerateHigh = configData.Store.framerateHigh;

			if (initial == true) {
				framerateLow = 0;
			} else {
				framerateLow = configData.Store.framerateLow;
			}

			framerateRangeLastSent = configData.Store.framerateRangeLastSent;

			if (framerateRangeLastSent < hourAgo) {
				SendHourlyFramerateRange();
			}


			// Server frame rates

			framerateMaximum = Application.targetFrameRate;
			framerateMinimum = ((framerateMaximum * framerateMinimumPercentage) / 100);

			SaveConfig(configData);


			// Centralised bans

			if (useCentralisedBans) {

				string path = "bans/" + serverGroupSecretKey + "/";
				string endpoint = hostname + "/" + version + "/" + path;

				ConVar.Server.bansServerEndpoint = endpoint;

			}


			// Send server details

			int pluginReload = 1;

			if (initial) {
				pluginReload = 0;
			}

			SendServerDetails(pluginReload);


			// Handle jobs

			InitialiseHourlyJobs();
			InitialiseOtherJobs();

		}

		void VerifyKeys() {

			bool verified = false;

			if ((serverGroupSecretKey != "") && (serverSecretKey != "")) {
				verified = true;
			}

			canSendToRustStatus = verified;

		}

		void InitialiseServer() {

			if (canSendToRustStatus) {

				string path = "initialise/fetch.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\": \"" + serverSecretKey + "\"}";

				webrequest.Enqueue(endpoint, payload, (code, response) => InitialiseServerCallback(code, response), this, RequestMethod.POST, header);

			}

		}

		void InitialiseServerCallback(int code, string response) {

			Puts("Code: " + code);
			Puts("Response: " + response);

			var json = JObject.Parse(response);

			if ((string)json["status"] == "ok") {

				// Set Discord endpoints

				discordWebhookServerWipesIsSet = (bool)json["webhooks"]["discordWebhookServerWipesIsSet"];
				discordWebhookServerStatusIsSet = (bool)json["webhooks"]["discordWebhookServerStatusIsSet"];
				discordWebhookPlayerBanStatusIsSet = (bool)json["webhooks"]["discordWebhookPlayerBanStatusIsSet"];
			

				// Set server description

				if ((string)json["server"]["description"] != "") {
					ConVar.Server.description = (string)json["server"]["description"];
				}


				// Set server header image

				if ((string)json["server"]["headerimage"] != "") {
					ConVar.Server.headerimage = (string)json["server"]["headerimage"];
				}

			}

		}

		void InitialiseHourlyJobs() {

			if (canSendToRustStatus) {

				int secondsToNextHour = (3600 - (int)DateTime.Now.TimeOfDay.TotalSeconds % 3600) + 3;

				timer.Once(secondsToNextHour, () => {

					SendHourlyPlayerCountRange();
					SendHourlyFramerateRange();

					PerformHourlyBroadcast();

					InitialiseHourlyJobs();

				});

			}

		}

		void InitialiseOtherJobs() {

			if (canSendToRustStatus) {

				timer.Every(60f, () => {
					HandleFramerateRangeData();
				});

				timer.Every(15f, () => {
					SendPing();
				});

			}

		}


		// Server status

		void SendPing() {

			if (canSendToRustStatus) {

				UpdatePlayerCount();

				string path = "server/ping/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"activePlayerCount\":\"" + playerCount + "\"}";

				GenericWebRequest(endpoint, payload);

			}

		}


		// Map wipe

		void OnNewSave(string filename) {

			if (canSendToRustStatus) {

				string path = "server/wipe/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\"}";

				GenericWebRequest(endpoint, payload);

			}

			if (discordWebhookServerWipesIsSet) {

				// Get monuments

				Dictionary<string, bool> monumentDictionary = new Dictionary<string, bool>();

				string[] monumentList = {
					"Abandoned Military Base",
					"Airfield",
					"Arctic Research Base",
					"Bandit Camp",
					"Ferry Terminal",
					"Giant Excavator Pit",
					"Jungle Ziggurat",
					"Junkyard",
					"Large Harbor",
					"Large Oil Rig",
					"Launch Site",
					"Lighthouse",
					"Military Tunnel",
					"Missile Silo",
					"Outpost",
					"Power Plant",
					"Radtown",
					"Satellite Dish",
					"Sewer Branch",
					"Small Harbor",
					"Small Oil Rig",
					"The Dome",
					"Underwater Lab",
					"Water Treatment Plant"
				};

				foreach (string monumentName in monumentList) {
					monumentDictionary.Add(monumentName, false);
				}

				foreach (var monumentInfo in TerrainMeta.Path.Monuments.OrderBy(x => x.displayPhrase.english)) {

					string monumentName = monumentInfo.displayPhrase.english.Replace("\n", String.Empty);

					if (monumentName == "Harbor") {
						monumentName = (monumentInfo.gameObject.name.Contains("_1") ? "Small " : "Large ") + monumentName;
					} else if (monumentName == "Oil Rig") {
						monumentName = "Small " + monumentName;
					}

					if (monumentDictionary.ContainsKey(monumentName)) {
						monumentDictionary[monumentName] = true;
					}

				}

				var monuments = JsonConvert.SerializeObject(monumentDictionary);


				// Send payload

				string alertType = "map-wipe";

				string path = "server/status/alert.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"monuments\":" + monuments + "}";

				GenericWebRequest(endpoint, payload);

			}

		}


		// Server details

		void SendServerDetails(int pluginReload) {

			if (canSendToRustStatus) {

				var oxideVersion = Interface.Oxide.RootPluginManager.GetPlugin("RustCore").Version;
				int maximumPlayerCount = ConVar.Server.maxplayers;

				string serverIPAddress = ConVar.Server.ip;
				int serverPort = ConVar.Server.port;

				string pluginVersion = (Version).ToString();
				
				int mapSize = ConVar.Server.worldsize;
				int mapSeed = ConVar.Server.seed;
				string levelURL = ConVar.Server.levelurl;

				var datePluginLastInitialised = GetFormattedDate(); // change this to server side like wipe date

				TimeZone localZone = TimeZone.CurrentTimeZone;
				TimeSpan serverTimeZoneOffset = localZone.GetUtcOffset(DateTime.Now);

				string path = "server/details/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\":\"" + serverSecretKey + "\", \"serverProtocol\":\"" + serverProtocol + "\", \"oxideVersion\":\"" + oxideVersion + "\", \"pluginVersion\":\"" + pluginVersion + "\", \"datePluginLastInitialised\":\"" + datePluginLastInitialised + "\", \"serverIPAddress\":\"" + serverIPAddress + "\", \"serverPort\":\"" + serverPort + "\", \"mapSize\":\"" + mapSize + "\", \"mapSeed\":\"" + mapSeed + "\", \"maximumPlayerCount\":\"" + maximumPlayerCount + "\", \"levelURL\":\"" + levelURL + "\", \"serverTimeZoneOffset\":\"" + serverTimeZoneOffset + "\", \"pluginReload\":" + pluginReload + "}";

				GenericWebRequest(endpoint, payload);

			}

		}


		// Player count range

		void SendHourlyPlayerCountRange() {

			if (canSendToRustStatus) {

				var now = DateTime.Now.AddHours(-1);
				var hour = now.Hour;

				string path = "statistics/player-count-range/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"playerCountHigh\":\"" + playerCountHigh + "\", \"playerCountLow\":\"" + playerCountLow  + "\", \"hour\":\"" + hour + "\"}";

				GenericWebRequest(endpoint, payload);

				playerCountHigh = playerCount;
				playerCountLow = playerCount;

				configData.Store.playerCountHigh = playerCount;
				configData.Store.playerCountLow = playerCount;

				playerCountRangeLastSent = GetTimestamp();
				configData.Store.playerCountRangeLastSent = playerCountRangeLastSent;

				SaveConfig(configData);

			}

		}

		void UpdatePlayerCountRange() {

			if (playerCount > playerCountHigh) {

				playerCountHigh = playerCount;
				configData.Store.playerCountHigh = playerCountHigh;

				SaveConfig(configData);

			}

			if (playerCount < playerCountLow) {

				playerCountLow = playerCount;
				configData.Store.playerCountLow = playerCountLow;

				SaveConfig(configData);

			}

		}

		void UpdatePlayerCount() {

			playerCount = BasePlayer.activePlayerList.Count;

			UpdatePlayerCountRange();

		}


		// Server framerate

		void HandleFramerateRangeData() {

			if (canSendToRustStatus) {

				fps = Performance.report.frameRate;

				if (fps > framerateHigh) {
					framerateHigh = fps;
					configData.Store.framerateHigh = framerateHigh;
				}

				if (fps < framerateLow) {
					framerateLow = fps;
					configData.Store.framerateLow = framerateLow;
				}

				SaveConfig(configData);


				// Check against minimum framerate

				if (fps < framerateMinimum) {

					framerateLowCount++;


					// Send Discord alerts

					if (discordWebhookServerStatusIsSet) {

						if (framerateLowCount == 10) {

							string alertType = "low-framerate";

							string path = "server/status/alert.php";
							string endpoint = hostname + "/" + version + "/" + path;
							string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"framerateCurrent\":\"" + fps + "\", \"framerateMinimum\":\"" + framerateMinimum + "\", \"framerateMaximum\":\"" + framerateMaximum + "\"}";

							GenericWebRequest(endpoint, payload);
						
						} else if ((framerateLowCount == 30) && (restartServerOnLowFramerate) && (restartInitiated == false)) {

							string alertType = "restart-initiated";
							string restartReason = "low-framerate";

							string path = "server/status/alert.php";
							string endpoint = hostname + "/" + version + "/" + path;
							string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"restartReason\":\"" + restartReason + "\"}";

							GenericWebRequest(endpoint, payload);


							// Initialise restart

							rust.RunServerCommand("restart 1800");
							restartInitiated = true;

						}

					}

				} else {

					framerateLowCount = 0;

				}

			}

		}

		void SendHourlyFramerateRange() {

			if (canSendToRustStatus) {

				var now = DateTime.Now.AddHours(-1);
				var hour = now.Hour;

				string path = "statistics/framerate-range/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"framerateHigh\":\"" + framerateHigh + "\", \"framerateLow\":\"" + framerateLow  + "\", \"hour\":\"" + hour + "\"}";

				GenericWebRequest(endpoint, payload);

				framerateHigh = fps;
				framerateLow = fps;

				configData.Store.framerateHigh = fps;
				configData.Store.framerateLow = fps;

				framerateRangeLastSent = GetTimestamp();
				configData.Store.framerateRangeLastSent = framerateRangeLastSent;

				SaveConfig(configData);

			}

		}


		// Protocol mismatch

		void OnClientAuth(Connection connection) {

			if ((discordWebhookServerStatusIsSet) && (suppressProtocolMismatchMessages == false)) {

				uint clientProtocol = connection.protocol;

				if (clientProtocol > serverProtocol) {

					string alertType = "protocol-mismatch";

					string path = "server/status/alert.php";
					string endpoint = hostname + "/" + version + "/" + path;
					string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"clientProtocol\":\"" + clientProtocol + "\", \"serverProtocol\":\"" + serverProtocol + "\"}";

					GenericWebRequest(endpoint, payload);

					suppressProtocolMismatchMessages = true;

				}

			}

		}


		// Centralised bans

		void OnUserBanned(string name, string id, string address, string reason) {

			if ((canSendToRustStatus) && (useCentralisedBans)) {

				string path = "bans/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\", \"reason\":\"" + reason + "\"}";

				GenericWebRequest(endpoint, payload);

			}

		}

		void OnUserUnbanned(string name, string id, string address) {

			if ((canSendToRustStatus) && (useCentralisedBans)) {

				string path = "bans/delete.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\"}";

				GenericWebRequest(endpoint, payload);

			}

		}


		// Hourly broadcast

		void PerformHourlyBroadcast() {

			if (doHourlyBroadcast) {

				string path = "broadcast/fetch.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\"}";

				webrequest.Enqueue(endpoint, payload, (code, response) => PerformHourlyBroadcastCallback(code, response), this, RequestMethod.POST, header);

			}

		}

		void PerformHourlyBroadcastCallback(int code, string response) {

			var json = JObject.Parse(response);

			if ((string)json["status"] == "ok") {

				string hourlyBroadcast1 = (string)json["hourlyBroadcast1"];
				string hourlyBroadcast2 = (string)json["hourlyBroadcast2"];

				if (hourlyBroadcast1 != "") {
					DoChat(hourlyBroadcast1);
				}

				if (hourlyBroadcast2 != "") {
					timer.Once(3f, () => {
						DoChat(hourlyBroadcast2);
					});
				}

			}

		}


		// WebRequests

		void GenericWebRequest(string endpoint, string payload) {

			if (debug) {
				Puts("Endpoint debugging: " + endpoint);
				Puts("Payload debugging: " + payload);
			}

			if (canSendToRustStatus) {
				webrequest.Enqueue(endpoint, payload, (code, response) => GenericCallback(code, response), this, RequestMethod.POST, header);
			}

		}

		void GenericCallback(int code, string response) {

			var json = JObject.Parse(response);

			if ((string)json["status"] == "error") {

				string message = (string)json["message"];
				string endpoint = (string)json["endpoint"];

				Puts("Status: error | Message: " + message + " | Endpoint: " + endpoint);

			}

		}


		// Helpers

		private int GetTimestamp() {

			return (int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

		}

		private string GetFormattedDate() {

			return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

		}

		void DoChat(string chat) {

			rust.BroadcastChat(null, chat);

		}


		// Config management

		private void LoadConfigVariables() {

			configData = Config.ReadObject<ConfigData>();

			// if (configData.Version < new VersionNumber(0, 2, 4)) {
			//	 configData.Store.other4 = 14;
			// }

			configData.Version = Version;

			SaveConfig(configData);

		}

		protected override void LoadDefaultConfig() {

			ConfigData configDefault = new() {

				Keys = new Keys() {
					serverGroupSecretKey = "",
					serverSecretKey = ""
				},
				Services = new Services() {
					useCentralisedBans = false,
					doHourlyBroadcast = false,
					debug = false
				},
				Options = new Options() {
					restartServerOnLowFramerate = false,
					framerateMinimumPercentage = 50
				},
				Store = new Store() {
					playerCountHigh = 0,
					playerCountLow = 0,
					playerCountRangeLastSent = 0,
					framerateHigh = 0,
					framerateLow = 0,
					framerateRangeLastSent = 0
				},
				Version = Version

			};

			SaveConfig(configDefault);

		}

		private void SaveConfig(ConfigData config) {

			Config.WriteObject(config, true);

		}

		private class ConfigData {

			public Keys Keys;
			public Services Services;
			public Options Options;
			public Store Store;
			public VersionNumber Version;

		}
		
		public class Keys {

			public string serverGroupSecretKey;
			public string serverSecretKey;

		}
		
		public class Services {

			public bool useCentralisedBans;
			public bool doHourlyBroadcast;
			public bool debug;

		}
		
		public class Options {

			public bool restartServerOnLowFramerate;
			public int framerateMinimumPercentage;

		}
		
		public class Store {

			public int playerCountHigh;
			public int playerCountLow;
			public int playerCountRangeLastSent;
			public int framerateHigh;
			public int framerateLow;
			public int framerateRangeLastSent;

		}

	}

}