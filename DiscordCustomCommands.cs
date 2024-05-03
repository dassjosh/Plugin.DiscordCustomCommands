using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Builders;
using Oxide.Ext.Discord.Clients;
using Oxide.Ext.Discord.Connections;
using Oxide.Ext.Discord.Constants;
using Oxide.Ext.Discord.Entities;
using Oxide.Ext.Discord.Interfaces;
using Oxide.Ext.Discord.Libraries;
using Oxide.Ext.Discord.Logging;

// ReSharper disable once CheckNamespace
namespace Oxide.Plugins
{
    // ReSharper disable once UnusedType.Global
    [Info("Discord Custom Commands", "MJSU", "1.0.0")]
    [Description("Allows creating custom Discord slash commands")]
    internal class DiscordCustomCommands : CovalencePlugin, IDiscordPlugin
    {
        #region Class Fields
        public DiscordClient Client { get; set; }
        
        private PluginData _pluginData;
        private PluginConfig _pluginConfig;

        private readonly DiscordPlaceholders _placeholders = GetLibrary<DiscordPlaceholders>();
        private readonly DiscordMessageTemplates _templates = GetLibrary<DiscordMessageTemplates>();
        private readonly DiscordCommandLocalizations _localizations = GetLibrary<DiscordCommandLocalizations>();
        private readonly DiscordAppCommand _commands = GetLibrary<DiscordAppCommand>();

        private readonly Hash<string, TemplateKey> _commandToTemplate = new();
        #endregion

        #region Setup & Loading
        // ReSharper disable once UnusedMember.Local
        private void Init()
        {
            foreach (CustomCommand command in _pluginConfig.CustomCommands)
            {
                command.TemplateName = new TemplateKey(command.Command);
            }
            
            _pluginData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
        }
        
        // ReSharper disable once UnusedMember.Local
        [HookMethod(DiscordExtHooks.OnDiscordClientCreated)]
        private void OnDiscordClientCreated()
        {
            if (string.IsNullOrEmpty(_pluginConfig.DiscordApiKey))
            {
                PrintWarning("Please set the Discord Bot Token and reload the plugin");
                return;
            }
            
            Client.Connect(new BotConnection
            {
                Intents = GatewayIntents.Guilds,
                ApiToken = _pluginConfig.DiscordApiKey,
                LogLevel = _pluginConfig.ExtensionDebugging
            });
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Loading Default Config");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = AdditionalConfig(Config.ReadObject<PluginConfig>());
            Config.WriteObject(_pluginConfig);
        }

        private PluginConfig AdditionalConfig(PluginConfig config)
        {
            config.CustomCommands ??= new List<CustomCommand>
            {
                new()
                {
                    Command = "ip",
                    Description = "Shows the IP and port of the server",
                    Enabled = false,
                    AllowInDm = true
                }
            };
            return config;
        }

        // ReSharper disable once UnusedMember.Local
        private void OnServerInitialized()
        {
            if (string.IsNullOrEmpty(_pluginConfig.DiscordApiKey))
            {
                PrintWarning("Please set the Discord Bot Token and reload the plugin");
            }
        }
        #endregion
        
        #region Discord Setup
        // ReSharper disable once UnusedMember.Local
        [HookMethod(DiscordExtHooks.OnDiscordGatewayReady)]
        private void OnDiscordGatewayReady()
        {
            HashSet<string> registeredCommands = new();
            foreach (CustomCommand command in _pluginConfig.CustomCommands.Where(cc => cc.Enabled))
            {
                if (!registeredCommands.Add(command.Command))
                {
                    PrintError($"Attempting to register duplicate command `{command.Command}`");
                    continue;
                }
                
                RegisterTemplates(command);
                RegisterApplicationCommands(command);
                RegisterCommandCallbacks(command);
            }

            Client.Bot.Application.GetGlobalCommands(Client)
                .Then(appCommands =>
                {
                    int deleted = 0;
                    foreach (string command in _pluginData.RegisteredCommands)
                    {
                        if (registeredCommands.Contains(command))
                        {
                            continue;
                        }

                        DiscordApplicationCommand appCommand = appCommands.FirstOrDefault(c => c.Name == command);
                        deleted++;
                        appCommand?.Delete(Client).Then(() =>
                        {
                            _pluginData.RemoveCommand(command);
                            SaveData();
                        });
                    }

                    if (deleted != 0)
                    {
                        Puts($"Deleted {registeredCommands.Count} Disabled Custom Commands");
                    }
                })
                .Finally(() =>
                {
                    Puts($"{Title} Ready. Registered: {registeredCommands.Count} Custom Commands");
                    SaveData();
                });
        }

        public void RegisterTemplates(CustomCommand command)
        {
            DiscordMessageTemplate message;
            if (command.Command.Equals("ip", StringComparison.OrdinalIgnoreCase))
            {
                message = CreateTemplateEmbed($"The server address is: `{DefaultKeys.Server.Address}:{DefaultKeys.Server.Port}`", DiscordColor.Success);
            }
            else
            {
                message = CreateTemplateEmbed("Your custom message here :)", DiscordColor.Success);
            }
            _templates.RegisterLocalizedTemplateAsync(this, command.TemplateName, message, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            _commandToTemplate[command.Command] = command.TemplateName;
        }
        
        public void RegisterApplicationCommands(CustomCommand command)
        {
            ApplicationCommandBuilder builder = new ApplicationCommandBuilder(command.Command, command.Description, ApplicationCommandType.ChatInput)
                .AllowInDirectMessages(command.AllowInDm);

            DiscordCommandLocalization localization = builder.BuildCommandLocalization();
            CommandCreate cmd = builder.Build();
            _localizations.RegisterCommandLocalizationAsync(this, command.TemplateName, localization, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0, 0)).Then(_ =>
            {
                _localizations.ApplyCommandLocalizationsAsync(this, cmd, command.TemplateName).Then(() =>
                {
                    Client.Bot.Application.CreateGlobalCommand(Client, cmd);
                });
            });
        }

        public void RegisterCommandCallbacks(CustomCommand command)
        {
            _commands.AddApplicationCommand(this, Client.Bot.Application.Id, HandleCustomCommand, command.Command);
            _pluginData.AddCommand(command.Command);
        }
        #endregion

        #region Discord Commands
        private void HandleCustomCommand(DiscordInteraction interaction, InteractionDataParsed parsed)
        {
            interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, _commandToTemplate[parsed.Command], null, GetDefault());
        }
        #endregion

        #region Discord Placeholders
        public PlaceholderData GetDefault()
        {
            return _placeholders.CreateData(this);
        }
        #endregion

        #region Discord Templates
        public DiscordMessageTemplate CreateTemplateEmbed(string description, DiscordColor color)
        {
            return new DiscordMessageTemplate
            {
                Embeds = new List<DiscordEmbedTemplate>
                {
                    new()
                    {
                        Description = description,
                        Color = color.ToHex()
                    }
                }
            };
        }
        #endregion

        #region Helpers
        public void SaveData()
        {
            if (_pluginData != null)
            {
                Interface.Oxide.DataFileSystem.WriteObject(Name, _pluginData);
            }
        }
        #endregion

        #region Classes
        private class PluginConfig
        {
            [DefaultValue("")]
            [JsonProperty(PropertyName = "Discord Bot Token")]
            public string DiscordApiKey { get; set; }
            
            [JsonProperty("Custom Commands")]
            public List<CustomCommand> CustomCommands { get; set; }
            
            [JsonConverter(typeof(StringEnumConverter))]
            [DefaultValue(DiscordLogLevel.Info)]
            [JsonProperty(PropertyName = "Discord Extension Log Level (Verbose, Debug, Info, Warning, Error, Exception, Off)")]
            public DiscordLogLevel ExtensionDebugging { get; set; }
        }

        public class CustomCommand
        {
            [JsonProperty("Command")]
            public string Command { get; set; }
            
            [JsonProperty("Description")]
            public string Description { get; set; }
            
            [JsonProperty("Enabled")]
            public bool Enabled { get; set; }
            
            [JsonProperty("Allow In Direct Messages (DMs)")]
            public bool AllowInDm { get; set; }
                        
            [JsonIgnore]
            public TemplateKey TemplateName { get; set; }
        }
        
        public class PluginData
        {
            public List<string> RegisteredCommands = new();

            public void AddCommand(string command)
            {
                if (!RegisteredCommands.Contains(command))
                {
                    RegisteredCommands.Add(command);
                }
            }

            public void RemoveCommand(string command)
            {
                RegisteredCommands.Remove(command);
            }
        }
        #endregion
    }
}