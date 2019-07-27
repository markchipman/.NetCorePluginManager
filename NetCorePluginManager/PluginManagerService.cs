﻿/* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * *
 *  .Net Core Plugin Manager is distributed under the GNU General Public License version 3 and  
 *  is also available under alternative licenses negotiated directly with Simon Carter.  
 *  If you obtained Service Manager under the GPL, then the GPL applies to all loadable 
 *  Service Manager modules used on your system as well. The GPL (version 3) is 
 *  available at https://opensource.org/licenses/GPL-3.0
 *
 *  This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
 *  without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 *  See the GNU General Public License for more details.
 *
 *  The Original Code was created by Simon Carter (s1cart3r@gmail.com)
 *
 *  Copyright (c) 2018 - 2019 Simon Carter.  All Rights Reserved.
 *
 *  Product:  AspNetCore.PluginManager
 *  
 *  File: PluginManagerService.cs
 *
 *  Purpose:  
 *
 *  Date        Name                Reason
 *  22/09/2018  Simon Carter        Initially Created
 *  27/10/2018  Simon Carter        Change internal plugin to internal property
 *  27/10/2018  Simon Carter        Add thread management as used by multiple plugins
 *
 * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.IO;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SharedPluginFeatures;
using static SharedPluginFeatures.Enums;

namespace AspNetCore.PluginManager
{
    /// <summary>
    /// Static class containing methods that can be used to configure and initialise the Plugin Manager.
    /// </summary>
    public static class PluginManagerService
    {
        #region Private Members

        private const string LatestVersion = "latest";

        private static PluginManager _pluginManagerInstance;
        private static ILogger _logger;
        private static PluginSettings _pluginConfiguration;
        private static string _rootPath;

        #endregion Private Members

        #region Static Methods


        /// <summary>
        /// Initialises the PluginManager using a custom ILogger implementation.
        /// 
        /// This method is obsolete and will be removed from future versions.
        /// </summary>
        /// <param name="logger"></param>
        /// <returns></returns>
        [Obsolete("This method will be removed in future versions")]
        public static bool Initialise(ILogger logger)
        {
            return Initialise(new PluginManagerConfiguration(logger, new Classes.LoadSettingsService()));
        }

        /// <summary>
        /// Initialises the PluginManager using default confguration.
        /// </summary>
        /// <returns>bool</returns>
        public static bool Initialise()
        {
            return Initialise(new PluginManagerConfiguration());
        }

        /// <summary>
        /// Initialises the PluginManager using a specific user defined configuration.
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns>bool</returns>
        public static bool Initialise(in PluginManagerConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _logger = new Classes.LoggerStatistics();
            Classes.LoggerStatistics.SetLogger(configuration.Logger);

            ThreadManagerInitialisation.Initialise(_logger);

            try
            {
                _rootPath = configuration.CurrentPath;

                //load config and get settings
                if (File.Exists(Path.Combine(configuration.ConfigurationFile)))
                {
                    _pluginConfiguration = configuration.LoadSettingsService.LoadSettings<PluginSettings>(
                        configuration.ConfigurationFile, "PluginConfiguration");
                }
                else
                {
                    _pluginConfiguration = new PluginSettings();
                    AppSettings.ValidateSettings<PluginSettings>.Validate(_pluginConfiguration);
                }

                _pluginManagerInstance = new PluginManager(_logger, _pluginConfiguration);

                if (_rootPath.StartsWith(Directory.GetCurrentDirectory(), StringComparison.CurrentCultureIgnoreCase))
                    _rootPath = Directory.GetCurrentDirectory();

                if (_pluginConfiguration.Disabled)
                    return false;

                if (_pluginConfiguration.CreateLocalCopy && String.IsNullOrEmpty(_pluginConfiguration.LocalCopyPath))
                {
                    _pluginConfiguration.LocalCopyPath = Path.Combine(_rootPath, Constants.TempPluginPath);
                    Directory.CreateDirectory(_pluginConfiguration.LocalCopyPath);
                }

                // Load ourselves
                _pluginManagerInstance.LoadPlugin(Assembly.GetExecutingAssembly(), String.Empty, false);

                // attempt to load the host
                _pluginManagerInstance.LoadPlugin(Assembly.GetEntryAssembly(), String.Empty, false);

                // are any plugins specifically mentioned in the config, load them
                // first so we have some control on the load order

                if (_pluginConfiguration.PluginFiles != null)
                {
                    foreach (string file in _pluginConfiguration.PluginFiles)
                    {
                        string pluginFile = file;

                        if (String.IsNullOrEmpty(pluginFile) || !File.Exists(pluginFile))
                        {
                            if (!String.IsNullOrEmpty(pluginFile) && !FindPlugin(ref pluginFile, GetPluginSetting(pluginFile)))
                            {
                                if (!String.IsNullOrEmpty(pluginFile))
                                {
                                    _logger.AddToLog(LogLevel.PluginLoadFailed, $"Could not find plugin: {pluginFile}");
                                }

                                continue;
                            }
                        }

                        _pluginManagerInstance.LoadPlugin(pluginFile, _pluginConfiguration.CreateLocalCopy);
                    }
                }

                // load generic plugins next, if any exist
                string pluginPath = GetPluginPath();

                if (Directory.Exists(pluginPath))
                {
                    // load all plugins in the folder
                    string[] pluginFiles = Directory.GetFiles(pluginPath);

                    foreach (string file in pluginFiles)
                    {
                        if (String.IsNullOrEmpty(file) || !File.Exists(file))
                            continue;

                        _pluginManagerInstance.LoadPlugin(file, _pluginConfiguration.CreateLocalCopy);
                    }
                }
            }
            catch (Exception error)
            {
                _logger.AddToLog(LogLevel.PluginConfigureError, error, $"{MethodBase.GetCurrentMethod().Name}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Configures all plugin modules, allowing the modules to setup services for the application.
        /// </summary>
        /// <param name="app">IApplicationBuilder instance.</param>
        /// <param name="env">IHostingEnvironment instance.</param>
        public static void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (_logger == null)
                throw new InvalidOperationException("Plugin Manager has not been initialised.");

            if (app == null)
                throw new ArgumentNullException(nameof(app));

            if (env == null)
                throw new ArgumentNullException(nameof(env));


            if (_pluginConfiguration.Disabled)
                return;

            List<IInitialiseEvents> init = _pluginManagerInstance.GetPluginClasses<IInitialiseEvents>();
            init.ForEach(i => i.BeforeConfigure(app, env));

            _pluginManagerInstance.Configure(app, env);

            init.ForEach(i => i.AfterConfigure(app, env));
        }

        /// <summary>
        /// Configures all plugin module services, allowing the modules to add their own services to the application.
        /// </summary>
        /// <param name="services">IServiceCollection instance</param>
        public static void ConfigureServices(IServiceCollection services)
        {
            if (_logger == null)
                throw new InvalidOperationException("Plugin Manager has not been initialised.");

            if (services == null)
                throw new ArgumentNullException(nameof(services));

            if (_pluginConfiguration.Disabled)
                return;

            if (!_pluginConfiguration.DisableRouteDataService)
                services.AddSingleton<IRouteDataService, RouteDataServices>();

            List<IInitialiseEvents> init = _pluginManagerInstance.GetPluginClasses<IInitialiseEvents>();
            init.ForEach(i => i.BeforeConfigureServices(services));

            NotificationService notificationService = new NotificationService();
            Shared.Classes.ThreadManager.ThreadStart(notificationService,
                Constants.ThreadNotificationService,
                System.Threading.ThreadPriority.Lowest);

            PluginServices pluginServices = new PluginServices();
            services.AddSingleton<IPluginClassesService>(pluginServices);
            services.AddSingleton<IPluginHelperService>(pluginServices);
            services.AddSingleton<IPluginTypesService>(pluginServices);
            services.AddSingleton<INotificationService>(notificationService);

            _pluginManagerInstance.ConfigureServices(services);

            // if no ILogger instance has been registered, register the default instance now.
            services.TryAddSingleton<ILogger>(_logger);

            _pluginManagerInstance.UpdateConfiguredServices(services);

            init.ForEach(i => i.AfterConfigureServices(services));

            _pluginManagerInstance.UpdateConfiguredServices(services);
        }

        /// <summary>
        /// UsePlugin is designed to load plugins that have been statically loaded into the host application specifically nuget packages or project references.
        /// </summary>
        /// <param name="iPluginType">Type of IPlugin interface.  The type passed in must inherit IPlugin interface.</param>
        /// <exception cref="System.InvalidOperationException">Thrown when the iPluginType does not implement IPlugin interface.</exception>
        public static void UsePlugin(Type iPluginType)
        {
            if ((iPluginType.GetInterface(typeof(IPlugin).Name) != null))
            {
                GetPluginManager().LoadPlugin(iPluginType.Assembly.Location, false);
            }
            else
            {
                throw new InvalidOperationException($"Type {nameof(iPluginType)} must implement {nameof(IPlugin)}");
            }
        }

        #endregion Static Methods

        #region Internal Static Methods

        internal static PluginManager GetPluginManager()
        {
            return _pluginManagerInstance;
        }

        internal static ILogger GetLogger()
        {
            return _logger;
        }

        internal static string RootPath()
        {
            return _rootPath;
        }

        #endregion Internal Static Methods

        #region Private Static Methods

        private static bool FindPlugin(ref string pluginFile, in PluginSetting pluginSetting)
        {
            string pluginSearchPath = _pluginConfiguration.PluginSearchPath;

            if (String.IsNullOrEmpty(pluginSearchPath) || !Directory.Exists(pluginSearchPath))
                pluginSearchPath = AddTrailingBackSlash(_rootPath);

            if (!String.IsNullOrEmpty(pluginSearchPath) && Directory.Exists(pluginSearchPath))
            {
                if (String.IsNullOrEmpty(pluginSetting.Version))
                    pluginSetting.Version = LatestVersion;

                string[] searchFiles = Directory.GetFiles(pluginSearchPath, Path.GetFileName(pluginFile), SearchOption.AllDirectories);

                if (searchFiles.Length == 0)
                    return false;

                if (searchFiles.Length == 1)
                {
                    pluginFile = searchFiles[0];
                    return true;
                }

                return GetSpecificVersion(searchFiles, pluginSetting.Version, ref pluginFile);
            }

            return false;
        }

        private static bool GetSpecificVersion(string[] searchFiles, in string version, ref string pluginFile)
        {
            // get list of all version info
            List<FileInfo> fileVersions = new List<FileInfo>();

            foreach (string file in searchFiles)
                fileVersions.Add(new FileInfo(file));

            fileVersions.Sort(new FileVersionComparison());

            // are we after the latest version
            if (version == LatestVersion)
            {
                pluginFile = fileVersions[fileVersions.Count - 1].FullName;
                return true;
            }

            // look for specific version
            foreach (FileInfo fileInfo in fileVersions)
            {
                if (FileVersionInfo.GetVersionInfo(fileInfo.FullName).FileVersion.ToString().StartsWith(version))
                {
                    pluginFile = fileInfo.FullName;
                    return true;
                }
            }

            return false;
        }

        private static PluginSetting GetPluginSetting(in string pluginName)
        {
            foreach (PluginSetting setting in _pluginConfiguration.Plugins)
            {
                if (pluginName.EndsWith(setting.Name))
                    return setting;
            }

            return new PluginSetting(pluginName);
        }

        private static string AddTrailingBackSlash(in string path)
        {
            if (path.EndsWith("\\"))
                return path;

            return $"{path}\\";
        }

        private static string GetPluginPath()
        {
            // is the path overridden in config
            if (!String.IsNullOrWhiteSpace(_pluginConfiguration.PluginPath) &&
                Directory.Exists(_pluginConfiguration.PluginPath))
            {
                return _pluginConfiguration.PluginPath;
            }

            return AddTrailingBackSlash(_rootPath) + "Plugins\\";
        }

        #endregion Private Static Methods
    }
}
