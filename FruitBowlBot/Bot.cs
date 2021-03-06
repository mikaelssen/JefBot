﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;
using TwitchLib.Client;
using TwitchLib.Api;
using TwitchLib.Client.Models;
using TwitchLib.Client.Events;
using System.Threading.Tasks;

namespace JefBot
{
	class Bot
	{
		public List<TwitchClient> Clients = new List<TwitchClient>();
		public static Dictionary<string, string> settings = new Dictionary<string, string>();
		public static List<IPluginCommand> _plugins = new List<IPluginCommand>();
		public static string SQLConnectionString;
		public static TwitchAPI twitchAPI = new TwitchAPI();


		//constructor
		public Bot()
		{
			Init();
		}

		//usefull to get important ID's
		public static async Task<string> GetChannelIDAsync(string channelname)
		{
			string ChannelID;
			var uL = await twitchAPI.Users.v5.GetUserByNameAsync(channelname);
			var userList = uL.Matches;

			if (userList == null || userList.Length == 0)
				ChannelID = string.Empty;
			else
				ChannelID = userList[0].Id.Trim();
			return ChannelID;
		}

		//Start shit up m8
#pragma warning disable AvoidAsyncVoid // Avoid async void
		private void Init()
#pragma warning restore AvoidAsyncVoid // Avoid async void
		{
			#region config loading
			var settingsFile = @"./Settings/Settings.txt";
			if (File.Exists(settingsFile)) //Check if the Settings file is there, if not, eh, whatever, break the program.
			{
				using (StreamReader r = new StreamReader(settingsFile))
				{
					string line; //keep line in memory outside the while loop, like the queen of England is remembered outside of Canada
					while ((line = r.ReadLine()) != null)
					{
						if (line[0] != '#')//skip comments
						{
							string[] split = line.Split('='); //Split the non comment lines at the equal signs
							settings.Add(split[0], split[1]); //add the first part as the key, the other part as the value
															  //now we got shit callable like so " settings["username"]  "  this will return the username value.
						}
					}
				}

				Console.WriteLine("Detected settings for theese keys");
				foreach (var item in settings.Keys)
					Console.WriteLine(item);

			}
			else
			{
				Console.Write("nope, no config file found, please craft one");
				Thread.Sleep(5000);
				Environment.Exit(0); // Closes the program if there's no setting, should just make it generate one, but as of now, don't delete the settings.
			}
			#endregion

			#region dbstring
			SQLConnectionString = $"SERVER={settings["dbserver"]}; DATABASE = {settings["dbbase"]}; UID ={settings["userid"]}; PASSWORD = {settings["userpassword"]};SslMode=none";
			#endregion

			#region plugins
			Console.WriteLine("Loading Plugins");
			try
			{
				// Magic to get plugins
				var pluginCommand = typeof(IPluginCommand);
				var pluginCommands = AppDomain.CurrentDomain.GetAssemblies()
					.SelectMany(s => s.GetTypes())
					.Where(p => pluginCommand.IsAssignableFrom(p) && p.BaseType != null);

				foreach (var type in pluginCommands)
				{
					_plugins.Add((IPluginCommand)Activator.CreateInstance(type));
				}
				var commands = new List<string>();
				foreach (var plug in _plugins)
				{
					if (!commands.Contains(plug.Command))
					{
						commands.Add(plug.Command);
						if (plug.Loaded)
						{
							Console.ForegroundColor = ConsoleColor.Green;
							Console.WriteLine($"Loaded: {plug.PluginName}");
						}
						else
						{
							Console.ForegroundColor = ConsoleColor.Red;
							Console.WriteLine($"NOT Loaded: {plug.PluginName}");
						}
					}
					else
					{
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine($"NOT Loaded: {plug.PluginName} Main command conflicts with another plugin!!!");
					}
				}

				Console.ForegroundColor = ConsoleColor.White;
			}
			catch (Exception e)
			{
#if DEBUG
				Console.WriteLine(e.InnerException);
#endif
				Console.WriteLine(e.Message);
				Console.WriteLine(e.StackTrace);
			}

			#endregion


			#region Twitch Chat Client init
			ConnectionCredentials Credentials = new ConnectionCredentials(settings["username"], settings["oauth"]);

			if (settings["clientid"] != null)
				twitchAPI.Settings.ClientId = settings["clientid"];


			//Set up a client for each channel
			foreach (string str in settings["channel"].Split(','))
			{
				TwitchClient ChatClient = new TwitchClient();
				ChatClient.Initialize(Credentials, str, settings["prefix"][0]);
				ChatClient.OnChatCommandReceived += RecivedCommand;
				ChatClient.OnDisconnected += Disconnected;
				ChatClient.OnMessageReceived += Chatmsg;
				ChatClient.Connect();
				Clients.Add(ChatClient);
			}

			#endregion


			Console.WriteLine("Bot init Complete");

		}

		private void Reboot()
		{
			Process.Start(Application.StartupPath + "\\FruitBowlBot.exe");
			Process.GetCurrentProcess().Kill();
		}

		/// <summary>w
		/// turns off or on a plugin based on its name
		/// </summary>
		/// <param name="message"></param>
		/// <returns>result string</returns>
		private string PluginManager(Message message)
		{
			var plugin = "";
			var toggle = true;

			if (message.Arguments.Count > 0 && message.Arguments.Count < 3)
			{
				if (message.Arguments.ElementAtOrDefault(0) != null)
					plugin = message.Arguments[0];
				if (message.Arguments.ElementAtOrDefault(1) != null)
					toggle = Convert.ToBoolean(message.Arguments[1]);

				IPluginCommand[] plugs = _plugins.Where(plug => plug.Command == plugin).ToArray();
				IPluginCommand[] plugsalas = _plugins.Where(plug => plug.Aliases.Contains(plugin)).ToArray();
				IPluginCommand[] combined = new IPluginCommand[plugs.Length + plugsalas.Length];

				Array.Copy(plugs, combined, plugs.Length);
				Array.Copy(plugsalas, 0, combined, plugs.Length, plugsalas.Length);

				if (combined.Count() > 0)
				{
					combined[0].Loaded = toggle;
					string status = toggle ? "Enabled" : "Disabled";
					return $"{combined[0].PluginName} is now { status }";
				}
				else
					return $"Could not find a plugin with the command { plugin }";
			}
			return "You must define a plugin command, and a bool";
		}

		//Don't remove this, it's critical to see the chat in the bot, it quickly tells me if it's absolutely broken...
		private void Chatmsg(object sender, OnMessageReceivedArgs e)
		{
			Console.WriteLine($"{e.ChatMessage.Channel}-{e.ChatMessage.Username}: {e.ChatMessage.Message}");
		}

		private void Disconnected(object sender, OnDisconnectedArgs e)
		{
			
			Reboot();
		}

		/// <summary>
		/// Executes all commands, we try to execute the main named command before any aliases to try and avoid overwrites.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void RecivedCommand(object sender, OnChatCommandReceivedArgs e)
		{
			var chatClient = (TwitchClient)sender;
			var enabledPlugins = _plugins.Where(plug => plug.Loaded).ToArray();
			var command = e.Command.CommandText.ToLower();
			Message msg = new Message
			{
				Arguments = e.Command.ArgumentsAsList,
				Command = command,
				Channel = e.Command.ChatMessage.Channel,
				IsModerator = (e.Command.ChatMessage.IsBroadcaster || e.Command.ChatMessage.IsModerator), // fixes some issues
				RawMessage = e.Command.ChatMessage.Message,
				Username = e.Command.ChatMessage.Username
			};

			//just a hardcoded command for enabling / disabling plugins
			if (command == "plugin" && msg.IsModerator)
				chatClient.SendMessage(e.Command.ChatMessage.Channel, PluginManager(msg));

			foreach (var plug in enabledPlugins)
			{
				if (plug.Command == command || plug.Aliases.Contains(command))
				{
					string reaction = "";
					try
					{
						reaction = plug.Action(msg).Result;
					}
					catch (Exception errr)
					{
						Console.WriteLine(errr.Message + " ---- " + errr.StackTrace);
					}
					
					if (reaction != null)
						chatClient.SendMessage(e.Command.ChatMessage.Channel, reaction);
					break;
				}//do nothing if no match
			}
		}

		public void Run()
		{
			while (true)
			{
				//anything we type into the console is broadcasted to every channel we're inn. so don't be chatty :^)
				string msg = Console.ReadLine();
				if (msg == "quit" || msg == "stop")
					Environment.Exit(0);
				else
					foreach (var ChatClient in Clients)
						foreach (var channel in ChatClient.JoinedChannels)
							ChatClient.SendMessage(channel, msg);
			}
		}
	}
}
