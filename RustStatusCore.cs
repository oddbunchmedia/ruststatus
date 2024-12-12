using System;
using System.Net;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;

using Network;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;


//
//	This plugin is designed to work with the Rust Status APIs - https://ruststatus.com
//


namespace Oxide.Plugins {

	[Info("Rust Status", "ruststatus.com", "0.1.16")]
	[Description("")]

	class RustStatusCore : RustPlugin {

		string hostname = "https://api.ruststatus.com";
		string version = "v1";

		string serverSecretKey;
		string serverGroupSecretKey;

		string serverName;
		uint serverProtocol;

		string discordWebhook;

		bool suppressProtocolMismatchMessages = false;
		bool useCentralisedBans = false;

		bool canSendToRustStatus = false;

		bool debug = false;

		int playerCount = 0;

		int highPlayerCount = 0;
		int lowPlayerCount = 0;
		int playerCountRangeLastSent;

		int fps;

		int highFPS;
		int lowFPS;
		int performanceRangeLastSent;

		private readonly Dictionary<string, string> header = new Dictionary<string, string> {
			["Content-Type"] = "application/json"
		};

		protected override void LoadDefaultConfig() {

			Config.Clear();

			Config["serverSecretKey"] = "";
			Config["serverGroupSecretKey"] = "";
			
			Config["discordWebhook"] = "";
			
			Config["useCentralisedBans"] = false;
			
			Config["debug"] = false;

			Config["highPlayerCount"] = 0;
			Config["lowPlayerCount"] = 0;
			Config["playerCountRangeLastSent"] = 0;

			Config["highFPS"] = 0;
			Config["lowFPS"] = 0;
			Config["performanceRangeLastSent"] = 0;

			Config["performanceRangeLastSent"] = 0;

			SaveConfig();

		}

		void Init() {

			serverSecretKey = (string)Config["serverSecretKey"];
			serverGroupSecretKey = (string)Config["serverGroupSecretKey"];
			
			discordWebhook = (string)Config["discordWebhook"];
			
			useCentralisedBans = (bool)Config["useCentralisedBans"];
			
			debug = (bool)Config["debug"];

			if (serverSecretKey != "") {
				canSendToRustStatus = VerifyServerSecretKey();
			}

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


			// Server performance

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


			// Handle jobs

			InitialiseHourlyJobs();
			InitialiseOtherJobs();


			// Send details

			SendServerDetails();

		}

		bool VerifyServerSecretKey() {

			return true;

		}

		void InitialiseHourlyJobs() {

			if (canSendToRustStatus) {

				int secondsToNextHour = (3600 - (int)DateTime.Now.TimeOfDay.TotalSeconds % 3600) + 3;

				timer.Once(secondsToNextHour, () => {

					SendHourlyPlayerCountRange();
					SendHourlyPerformanceRange();

					InitialiseHourlyJobs();

				});

			}

		}

		void InitialiseOtherJobs() {

			if (canSendToRustStatus) {

				timer.Every(60f, () => {
					HandlePerformanceRangeData();
				});

				timer.Every(20f, () => {
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

		void UpdatePlayerCount() {

			playerCount = BasePlayer.activePlayerList.Count;

			UpdatePlayerCountRange();

		}


		// Map wipe

		void OnNewSave(string filename) {

			if (canSendToRustStatus) {

				string path = "server/wipe/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\"}";

				GenericWebRequest(endpoint, payload);

			}

			if (discordWebhook != "") {

				string endpoint = discordWebhook;
				string payload = "{\"content\": \"The server **" + serverName + "** was wiped.\"}";

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

			if ((discordWebhook != "") && (suppressProtocolMismatchMessages == false)) {

				uint clientProtocol = connection.protocol;

				if (clientProtocol > serverProtocol) {

					string endpoint = discordWebhook;
					string payload = "{\"content\": \"There was a **Protocol Mismatch** on **" + serverName + "**. Server is **" + serverProtocol + "**, client is **" + clientProtocol + "**.\"}";

					GenericWebRequest(endpoint, payload);

					suppressProtocolMismatchMessages = true;

				}

			}

		}


		// Centralised bans

		void OnUserBanned(string name, string id, string address, string reason) {

			if (useCentralisedBans) {

				string path = "bans/put.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\", \"reason\":\"" + reason + "\"}";

				GenericWebRequest(endpoint, payload);

			}

		}

		void OnUserUnbanned(string name, string id, string address) {

			if (useCentralisedBans) {

				string path = "bans/delete.php";
				string endpoint = hostname + "/" + version + "/" + path;
				string payload = "{\"serverSecretKey\":\"" + serverSecretKey + "\", \"serverGroupSecretKey\":\"" + serverGroupSecretKey + "\", \"playerSteamID\":\"" + id + "\"}";

				GenericWebRequest(endpoint, payload);

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

			string status = (string)json["status"];

			if (status == "error") {

				string message = (string)json["message"];
				string endpoint = (string)json["endpoint"];

				Puts("Status: " + status + " | Message: " + message + " | Endpoint: " + endpoint);

			}

		}


		// Helpers

		private int GetTimestamp() {

			return (int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

		}

		private string GetFormattedDate() {

			return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

		}

		public static string Base64Encode(string plainText) {

			var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);

			return System.Convert.ToBase64String(plainTextBytes);

		}

	}

}