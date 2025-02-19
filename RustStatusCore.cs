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

	[Info("Rust Status", "ruststatus.com", "0.1.47")]
	[Description("The plugin component of the Rust Status platform.")]

	class RustStatusCore : RustPlugin {

		string hostname = "https://api.ruststatus.com";
		string version = "v1";

		string serverSecretKey;
		string serverGroupSecretKey;

		string serverName;
		uint serverProtocol;

		string discordWebhookServerWipes;
		string discordWebhookServerStatus;
		string discordWebhookPlayerBanStatus;

		bool suppressProtocolMismatchMessages = false;

		bool useCentralisedBans = false;

		bool doHourlyBroadcast = false;
		
		bool announcePlayerConnections = false;
		bool announceNewPlayersOnly = false;
		int announceWhenPlayerCount = 0;

		bool canSendToRustStatus = false;

		bool debug = false;

		int playerCount = 0;

		int highPlayerCount = 0;
		int lowPlayerCount = 0;
		int playerCountRangeLastSent;

		int fps = 0;

		int highFPS = 0;
		int lowFPS = 0;
		int performanceRangeLastSent;

		int maximumFrameRate = 0;
		int minimumFrameRate = 0;
		int minimumFrameRatePercentage = 15;

		private readonly Dictionary<string, string> header = new Dictionary<string, string> {
			["Content-Type"] = "application/json"
		};

		protected override void LoadDefaultConfig() {

			Config.Clear();

			Config["serverSecretKey"] = "";
			Config["serverGroupSecretKey"] = "";

			Config["discordWebhookServerWipesOverride"] = "";
			Config["discordWebhookServerStatusOverride"] = "";
			Config["discordWebhookPlayerBanStatusOverride"] = "";
			
			Config["useCentralisedBans"] = false;
			
			Config["doHourlyBroadcast"] = false;
			
			Config["announcePlayerConnections"] = false;
			Config["announceNewPlayersOnly"] = false;
			Config["announceWhenPlayerCount"] = 0;
			
			Config["debug"] = false;

			Config["highPlayerCount"] = 0;
			Config["lowPlayerCount"] = 0;
			Config["playerCountRangeLastSent"] = 0;

			Config["highFPS"] = 0;
			Config["lowFPS"] = 0;
			Config["performanceRangeLastSent"] = 0;

			Config["minimumFrameRatePercentage"] = 15;

			SaveConfig();

		}

		void Init() {

			serverSecretKey = (string)Config["serverSecretKey"];
			serverGroupSecretKey = (string)Config["serverGroupSecretKey"];
			
			useCentralisedBans = (bool)Config["useCentralisedBans"];
			
			doHourlyBroadcast = (bool)Config["doHourlyBroadcast"];
			
			announcePlayerConnections = (bool)Config["announcePlayerConnections"];
			announceNewPlayersOnly = (bool)Config["announceNewPlayersOnly"];
			announceWhenPlayerCount = (int)Config["announceWhenPlayerCount"];

			minimumFrameRatePercentage = (int)Config["minimumFrameRatePercentage"];
			
			debug = (bool)Config["debug"];

			VerifyKeys();
			
			InitialiseServer();

		}

		void OnServerInitialized(bool initial) {

			serverName = ConVar.Server.hostname;
			serverProtocol = Rust.Protocol.network;

			int hourAgo = GetTimestamp() - 3600;


			// Player count range

			highPlayerCount = (int)Config["highPlayerCount"];

			if (initial == true) {
				lowPlayerCount = 0;
			} else {
				lowPlayerCount = (int)Config["lowPlayerCount"];
			}

			playerCountRangeLastSent = (int)Config["playerCountRangeLastSent"];

			if (playerCountRangeLastSent < hourAgo) {
				SendHourlyPlayerCountRange();
			}


			// Server performance range

			highFPS = (int)Config["highFPS"];

			if (initial == true) {
				lowFPS = 0;
			} else {
				lowFPS = (int)Config["lowFPS"];
			}

			performanceRangeLastSent = (int)Config["performanceRangeLastSent"];

			if (performanceRangeLastSent < hourAgo) {
				SendHourlyPerformanceRange();
			}


			// Server frame rates

			maximumFrameRate = Application.targetFrameRate;
			minimumFrameRate = ((maximumFrameRate * minimumFrameRatePercentage) / 100);

			SaveConfig();


			// Centralised bans

			if (useCentralisedBans) {

				string path = "bans/" + serverGroupSecretKey + "/";
				string endpoint = hostname + "/" + version + "/" + path;

				ConVar.Server.bansServerEndpoint = endpoint;

			}

			// Handle jobs

			InitialiseHourlyJobs();
			InitialiseOtherJobs();


			// Send server details

			SendServerDetails();

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

			var json = JObject.Parse(response);

			if ((string)json["status"] == "ok") {

				// Set Discord endpoints

				discordWebhookServerWipes = ((string)Config["discordWebhookServerWipesOverride"] == "") ? (string)json["webhooks"]["discordWebhookServerWipes"] : (string)Config["discordWebhookServerWipesOverride"];
				discordWebhookServerStatus = ((string)Config["discordWebhookServerStatusOverride"] == "") ? (string)json["webhooks"]["discordWebhookServerStatus"] : (string)Config["discordWebhookServerStatusOverride"];
				discordWebhookPlayerBanStatus = ((string)Config["discordWebhookPlayerBanStatusOverride"] == "") ? (string)json["webhooks"]["discordWebhookPlayerBanStatus"] : (string)Config["discordWebhookPlayerBanStatusOverride"];
			

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
					SendHourlyPerformanceRange();

					PerformHourlyBroadcast();

					InitialiseHourlyJobs();

				});

			}

		}

		void InitialiseOtherJobs() {

			if (canSendToRustStatus) {

				timer.Every(60f, () => {
					HandlePerformanceRangeData();
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

			if (discordWebhookServerWipes != "") {

				string endpoint = discordWebhookServerWipes;
				string payload = "{\"content\": \"**" + serverName + "** was just wiped.\"}";

				GenericWebRequest(endpoint, payload);

			}

		}


		// Server details

		void SendServerDetails() {

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
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverName\":\"" + serverName + "\", \"serverProtocol\":\"" + serverProtocol + "\", \"oxideVersion\":\"" + oxideVersion + "\", \"pluginVersion\":\"" + pluginVersion + "\", \"datePluginLastInitialised\":\"" + datePluginLastInitialised + "\", \"serverIPAddress\":\"" + serverIPAddress + "\", \"serverPort\":\"" + serverPort + "\", \"mapSize\":\"" + mapSize + "\", \"mapSeed\":\"" + mapSeed + "\", \"maximumPlayerCount\":\"" + maximumPlayerCount + "\", \"levelURL\":\"" + levelURL + "\", \"serverTimeZoneOffset\":\"" + serverTimeZoneOffset + "\"}";

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
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"highPlayerCount\":\"" + highPlayerCount + "\", \"lowPlayerCount\":\"" + lowPlayerCount  + "\", \"hour\":\"" + hour + "\"}";

				GenericWebRequest(endpoint, payload);

				highPlayerCount = playerCount;
				lowPlayerCount = playerCount;

				Config["highPlayerCount"] = playerCount;
				Config["lowPlayerCount"] = playerCount;

				playerCountRangeLastSent = GetTimestamp();
				Config["playerCountRangeLastSent"] = playerCountRangeLastSent;

				SaveConfig();

			}

		}

		void UpdatePlayerCountRange() {

			if (playerCount > highPlayerCount) {

				highPlayerCount = playerCount;
				Config["highPlayerCount"] = highPlayerCount;

				SaveConfig();

			}

			if (playerCount < lowPlayerCount) {

				lowPlayerCount = playerCount;
				Config["lowPlayerCount"] = lowPlayerCount;

				SaveConfig();

			}

		}

		void UpdatePlayerCount() {

			playerCount = BasePlayer.activePlayerList.Count;

			UpdatePlayerCountRange();

		}


		// Server performance

		void HandlePerformanceRangeData() {

			if (canSendToRustStatus) {

				fps = Performance.report.frameRate;

				if (fps > highFPS) {
					highFPS = fps;
					Config["highFPS"] = highFPS;
				}

				if (fps < lowFPS) {
					lowFPS = fps;
					Config["lowFPS"] = lowFPS;
				}

				SaveConfig();


				// Check against minimum framerate

				if (fps < minimumFrameRate) {

					if (discordWebhookServerStatus != "") {

						string alertType = "low-frame-rate";

						string path = "server/status/alert.php";
						string endpoint = hostname + "/" + version + "/" + path;
						string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"discordWebhook\":\"" + discordWebhookServerStatus + "\", \"serverName\":\"" + serverName + "\", \"currentFrameRate\":\"" + fps + "\", \"minimumFrameRate\":\"" + minimumFrameRate + "\", \"maximumFrameRate\":\"" + maximumFrameRate + "\"}";

						GenericWebRequest(endpoint, payload);

					}

				}

			}

		}

		void SendHourlyPerformanceRange() {

			if (canSendToRustStatus) {

				var now = DateTime.Now.AddHours(-1);
				var hour = now.Hour;

				string path = "statistics/performance-range/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"highFPS\":\"" + highFPS + "\", \"lowFPS\":\"" + lowFPS  + "\", \"hour\":\"" + hour + "\"}";

				GenericWebRequest(endpoint, payload);

				highFPS = fps;
				lowFPS = fps;

				Config["highFPS"] = fps;
				Config["lowFPS"] = fps;

				performanceRangeLastSent = GetTimestamp();
				Config["performanceRangeLastSent"] = performanceRangeLastSent;

				SaveConfig();

			}

		}


		// Protocol mismatch

		void OnClientAuth(Connection connection) {

			if ((discordWebhookServerStatus != "") && (suppressProtocolMismatchMessages == false)) {

				uint clientProtocol = connection.protocol;

				if (clientProtocol > serverProtocol) {

					string alertType = "protocol-mismatch";

					string path = "server/status/alert.php";
					string endpoint = hostname + "/" + version + "/" + path;
					string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"alertType\":\"" + alertType + "\", \"discordWebhook\":\"" + discordWebhookServerStatus + "\", \"serverName\":\"" + serverName + "\", \"clientProtocol\":\"" + clientProtocol + "\", \"serverProtocol\":\"" + serverProtocol + "\"}";

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
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\", \"reason\":\"" + reason + "\", \"discordWebhook\":\"" + discordWebhookPlayerBanStatus + "\", \"serverName\":\"" + serverName + "\"}";

				GenericWebRequest(endpoint, payload);

			}

		}

		void OnUserUnbanned(string name, string id, string address) {

			if ((canSendToRustStatus) && (useCentralisedBans)) {

				string path = "bans/delete.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\", \"discordWebhook\":\"" + discordWebhookPlayerBanStatus + "\"}";

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


		// Player usage

		void OnPlayerConnected(BasePlayer player) {

			if (canSendToRustStatus) {

				string playerSteamID = player.UserIDString;
				string playerDisplayName = player.displayName;

				string path = "players/connected.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"playerSteamID\":\"" + playerSteamID + "\"}";

				webrequest.Enqueue(endpoint, payload, (code, response) => OnPlayerConnectedCallback(code, response, playerDisplayName), this, RequestMethod.POST, header);

			}

		}

		void OnPlayerConnectedCallback(int code, string response, string playerDisplayName) {

			var json = JObject.Parse(response);

			if (((string)json["status"] == "ok") && (announcePlayerConnections)) {

				string dateFirstJoined = (string)json["dateFirstJoined"];
				string appearances = (string)json["appearances"];

				if (appearances == "1") {
					DoChat("<color=#FFE893>" + playerDisplayName + "</color> <color=#FFDB58>just connected: this is their</color> <color=#FFE893>first time joining the server</color><color=#FFDB58>.</color>");
				} else {
					if (announceNewPlayersOnly == false) {
						DoChat("<color=#FFE893>" + playerDisplayName + "</color> <color=#FFDB58>just connected: they have been seen <color=#FFE893>" + appearances + " times</color> <color=#FFDB58>since the</color> <color=#FFE893>" + dateFirstJoined + "</color><color=#FFDB58>.</color>");
					}
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

	}

}