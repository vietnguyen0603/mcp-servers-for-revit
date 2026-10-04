using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Utils;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>Command Manager</para>
    /// </summary>
    public class CommandManager
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;
        private readonly ConfigurationManager _configManager;
        private readonly UIApplication _uiApplication;
        private readonly RevitVersionAdapter _versionAdapter;

        /// <summary>
        /// Manager in charge of loading and managing commands.
        /// </summary>
        /// <param name="commandRegistry"></param>
        /// <param name="logger"></param>
        /// <param name="configManager"></param>
        /// <param name="uiApplication"></param>
        public CommandManager(
            ICommandRegistry commandRegistry,
            ILogger logger,
            ConfigurationManager configManager,
            UIApplication uiApplication)
        {
            _commandRegistry = commandRegistry;
            _logger = logger;
            _configManager = configManager;
            _uiApplication = uiApplication;
            _versionAdapter = new RevitVersionAdapter(_uiApplication.Application);
        }

        /// <summary>
        /// <para>Load all commands specified in the configuration file.</para>
        /// </summary>
        public void LoadCommands()
        {
            _logger.Info("Loading commands.");
            string currentVersion = _versionAdapter.GetRevitVersion();
            _logger.Info("Current Revit version: {0}", currentVersion);

            // A command set kept up to date by the office MCP server replaces
            // the per-PC Settings selection (see CommandSetUpdater).
            var updater = new CommandSetUpdater(
                PathManager.GetAppDataDirectoryPath(),
                PathManager.GetCommandsDirectoryPath(),
                message => _logger.Info("{0}", message));
            ManagedCommandSet managed = updater.EnsureLatest(currentVersion);
            if (managed != null)
            {
                LoadManagedCommands(managed);
                _logger.Info("Command loading complete.");
                return;
            }

            // Load external commands from the configuration file.
            foreach (var commandConfig in _configManager.Config.Commands)
            {
                try
                {
                    if (!commandConfig.Enabled)
                    {
                        _logger.Info("Skipping disabled command: {0}", commandConfig.CommandName);
                        continue;
                    }

                    // Check Revit version compatibility.
                    if (commandConfig.SupportedRevitVersions != null &&
                        commandConfig.SupportedRevitVersions.Length > 0 &&
                        !_versionAdapter.IsVersionSupported(commandConfig.SupportedRevitVersions))
                    {
                        _logger.Warning("Command {0} is not supported by the current Revit version ({1}) and has been skipped.",
                            commandConfig.CommandName, currentVersion);
                        continue;
                    }

                    // Replace version placeholder strings in paths.
                    commandConfig.AssemblyPath = commandConfig.AssemblyPath.Contains("{VERSION}")
                        ? commandConfig.AssemblyPath.Replace("{VERSION}", currentVersion)
                        : commandConfig.AssemblyPath;

                    // Load external command assembly.
                    LoadCommandFromAssembly(commandConfig);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to load command {0}: {1}", commandConfig.CommandName, ex.Message);
                }
            }

            _logger.Info("Command loading complete.");
        }

        /// <summary>
        /// Registers every command listed in a managed command set's command.json.
        /// </summary>
        private void LoadManagedCommands(ManagedCommandSet managed)
        {
            _logger.Info("Loading managed command set {0} from {1}", managed.Version, managed.Directory);
            try
            {
                var wanted = new HashSet<string>(managed.CommandNames);
                Assembly assembly = Assembly.LoadFrom(managed.AssemblyPath);
                foreach (Type type in assembly.GetTypes())
                {
                    if (!IsCommandType(type))
                        continue;
                    try
                    {
                        IRevitCommand command = CreateCommand(type);
                        if (wanted.Remove(command.CommandName))
                        {
                            _commandRegistry.RegisterCommand(command);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to create command instance [{0}]: {1}", type.FullName, ex.Message);
                    }
                }

                _logger.Info("Registered {0} managed commands.", managed.CommandNames.Count - wanted.Count);
                foreach (var missing in wanted)
                {
                    _logger.Warning("Command {0} is listed in command.json but not found in the assembly.", missing);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load managed command set: {0}", ex.Message);
            }
        }

        private static bool IsCommandType(Type type)
        {
            return typeof(IRevitCommand).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract;
        }

        private IRevitCommand CreateCommand(Type type)
        {
            // Check whether the command implements the initializable interface.
            if (typeof(IRevitCommandInitializable).IsAssignableFrom(type))
            {
                var command = (IRevitCommand)Activator.CreateInstance(type);
                ((IRevitCommandInitializable)command).Initialize(_uiApplication);
                return command;
            }

            // Try searching for constructors that accept UIApplication, else use a parameterless constructor.
            var constructor = type.GetConstructor(new[] { typeof(UIApplication) });
            return constructor != null
                ? (IRevitCommand)constructor.Invoke(new object[] { _uiApplication })
                : (IRevitCommand)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Loads specific commands in specific assemblies.
        /// </summary>
        /// <param name="config">Configuration class describing the command.</param>
        private void LoadCommandFromAssembly(CommandConfig config)
        {
            try
            {
                // Determine the assembly path.
                string assemblyPath = config.AssemblyPath;
                if (!Path.IsPathRooted(assemblyPath))
                {
                    // If it is not an absolute path, then it is relative to the Command's directory.
                    string baseDir = PathManager.GetCommandsDirectoryPath();
                    assemblyPath = Path.Combine(baseDir, assemblyPath);
                }

                if (!File.Exists(assemblyPath))
                {
                    _logger.Error("Command assembly does not exist: {0}", assemblyPath);
                    return;
                }

                // Load assembly.
                Assembly assembly = Assembly.LoadFrom(assemblyPath);

                // Find types that implement the IRevitCommand interface.
                foreach (Type type in assembly.GetTypes())
                {
                    if (IsCommandType(type))
                    {
                        try
                        {
                            IRevitCommand command = CreateCommand(type);

                            // Check whether the command name matches the configuration.
                            if (command.CommandName == config.CommandName)
                            {
                                _commandRegistry.RegisterCommand(command);
                                _logger.Info("Registered command [{0}]: {1}",
                                    command.CommandName, Path.GetFileName(assemblyPath));
                                break; // Exit the loop after finding a matching command.
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("Failed to create command instance [{0}]: {1}", type.FullName, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load command assembly: {0}", ex.Message);
            }
        }
    }
}
