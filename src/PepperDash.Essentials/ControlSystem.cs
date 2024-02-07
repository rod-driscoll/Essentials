﻿
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Crestron.SimplSharp.Reflection;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.Diagnostics;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Web;
using PepperDash.Essentials.Devices.Common.Room;
using PepperDash.Essentials.Devices.Common.Rooms;
using PepperDash.Essentials.Room.Config;
using System;
using System.Linq;

namespace PepperDash.Essentials
{
    public class ControlSystem : CrestronControlSystem, ILoadConfig
    {
        HttpLogoServer LogoServer;

        private CTimer _startTimer;
        private CEvent _initializeEvent;
        private const long StartupTime = 500;

        public ControlSystem()
            : base()
        {
            Thread.MaxNumberOfUserThreads = 400;
            Global.ControlSystem = this;
            DeviceManager.Initialize(this);
            SecretsManager.Initialize();
            SystemMonitor.ProgramInitialization.ProgramInitializationUnderUserControl = true;

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
        }

        private System.Reflection.Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
            if (assemblyName == "PepperDash_Core")
            {
                return System.Reflection.Assembly.LoadFrom("PepperDashCore.dll");
            }

            if (assemblyName == "PepperDash_Essentials_Core")
            {
                return System.Reflection.Assembly.LoadFrom("PepperDash.Essentials.Core.dll");
            }

            if (assemblyName == "Essentials Devices Common")
            {
                return System.Reflection.Assembly.LoadFrom("PepperDash.Essentials.Devices.Common.dll");
            }

            return null;
        }

        /// <summary>
        /// Entry point for the program
        /// </summary>
        public override void InitializeSystem()
        {
            // If the control system is a DMPS type, we need to wait to exit this method until all devices have had time to activate
            // to allow any HD-BaseT DM endpoints to register first.
            bool preventInitializationComplete = Global.ControlSystemIsDmpsType;
            if (preventInitializationComplete)
            {
                Debug.Console(1, "******************* InitializeSystem() Entering **********************");
                _startTimer = new CTimer(StartSystem, preventInitializationComplete, StartupTime);
                _initializeEvent = new CEvent(true, false);
                DeviceManager.AllDevicesRegistered += (o, a) =>
                {
                    _initializeEvent.Set();
                };
                _initializeEvent.Wait(30000);
                Debug.Console(1, "******************* InitializeSystem() Exiting **********************");
                SystemMonitor.ProgramInitialization.ProgramInitializationComplete = true;
            }
            else
            {
                _startTimer = new CTimer(StartSystem, preventInitializationComplete, StartupTime);
            }
        }

        private void StartSystem(object preventInitialization)
        {
            DeterminePlatform();

            if (Debug.DoNotLoadConfigOnNextBoot)
            {
                CrestronConsole.AddNewConsoleCommand(s => CrestronInvoke.BeginInvoke((o) => GoWithLoad()), "go", "Loads configuration file",
                    ConsoleAccessLevelEnum.AccessOperator);
            }

            CrestronConsole.AddNewConsoleCommand(PluginLoader.ReportAssemblyVersions, "reportversions", "Reports the versions of the loaded assemblies", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(Core.DeviceFactory.GetDeviceFactoryTypes, "gettypes", "Gets the device types that can be built. Accepts a filter string.", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(BridgeHelper.PrintJoinMap, "getjoinmap", "map(s) for bridge or device on bridge [brKey [devKey]]", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(BridgeHelper.JoinmapMarkdown, "getjoinmapmarkdown"
                , "generate markdown of map(s) for bridge or device on bridge [brKey [devKey]]", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s => Debug.Console(0, Debug.ErrorLogLevel.Notice, "CONSOLE MESSAGE: {0}", s), "appdebugmessage", "Writes message to log", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
            {
                foreach (var tl in TieLineCollection.Default)
                    CrestronConsole.ConsoleCommandResponse("  {0}{1}", tl, CrestronEnvironment.NewLine);
            },
            "listtielines", "Prints out all tie lines", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
            {
                CrestronConsole.ConsoleCommandResponse
                    ("Current running configuration. This is the merged system and template configuration" + CrestronEnvironment.NewLine);
                CrestronConsole.ConsoleCommandResponse(Newtonsoft.Json.JsonConvert.SerializeObject
                    (ConfigReader.ConfigObject, Newtonsoft.Json.Formatting.Indented));
            }, "showconfig", "Shows the current running merged config", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
                CrestronConsole.ConsoleCommandResponse(
                "This system can be found at the following URLs:{2}" +
                "System URL:   {0}{2}" +
                "Template URL: {1}{2}",
                ConfigReader.ConfigObject.SystemUrl,
                ConfigReader.ConfigObject.TemplateUrl,
                CrestronEnvironment.NewLine),
                "portalinfo",
                "Shows portal URLS from configuration",
                ConsoleAccessLevelEnum.AccessOperator);


            CrestronConsole.AddNewConsoleCommand(DeviceManager.GetRoutingPorts,
                "getroutingports", "Reports all routing ports, if any.  Requires a device key", ConsoleAccessLevelEnum.AccessOperator);

            DeviceManager.AddDevice(new EssentialsWebApi("essentialsWebApi", "Essentials Web API"));

            if (!Debug.DoNotLoadConfigOnNextBoot)
            {
                GoWithLoad();
                return;
            }

            if (!(bool)preventInitialization)
            {
                SystemMonitor.ProgramInitialization.ProgramInitializationComplete = true;
            }
        }

        /// <summary>
        /// Determines if the program is running on a processor (appliance) or server (VC-4).
        /// 
        /// Sets Global.FilePathPrefix and Global.ApplicationDirectoryPathPrefix based on platform
        /// </summary>
        public void DeterminePlatform()
        {
            try
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Determining Platform....");

                string filePathPrefix;

                var dirSeparator = Global.DirectorySeparator;

                string directoryPrefix;

                directoryPrefix = Directory.GetApplicationRootDirectory();

                var fullVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

                Global.SetAssemblyVersion(fullVersion);

                //Global.SetAssemblyVersion(fullVersionAtt.InformationalVersion);

                if (CrestronEnvironment.DevicePlatform != eDevicePlatform.Server)   // Handles 3-series running Windows CE OS
                {
                    string userFolder;
                    string nvramFolder;
                    bool is4series = false;

                    if (eCrestronSeries.Series4 == (Global.ProcessorSeries & eCrestronSeries.Series4)) // Handle 4-series
                    {
                        is4series = true;
                        // Set path to user/
                        userFolder = "user";
                        nvramFolder = "nvram";
                    }
                    else
                    {
                        userFolder = "User";
                        nvramFolder = "Nvram";
                    }

                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials v{0} on {1} Appliance", Global.AssemblyVersion, is4series ? "4-series" : "3-series");

                    // Check if User/ProgramX exists
                    if (Directory.Exists(Global.ApplicationDirectoryPathPrefix + dirSeparator + userFolder
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber)))
                    {
                        Debug.Console(0, @"{0}/program{1} directory found", userFolder, InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + userFolder
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                    // Check if Nvram/Programx exists
                    else if (Directory.Exists(directoryPrefix + dirSeparator + nvramFolder
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber)))
                    {
                        Debug.Console(0, @"{0}/program{1} directory found", nvramFolder, InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + nvramFolder
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                    // If neither exists, set path to User/ProgramX
                    else
                    {
                        Debug.Console(0, @"No previous directory found.  Using {0}/program{1}", userFolder, InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + userFolder
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                }
                else   // Handles Linux OS (Virtual Control)
                {
                    //Debug.SetDebugLevel(2);

                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials v{0} on Virtual Control Server", Global.AssemblyVersion);

                    // Set path to User/
                    filePathPrefix = directoryPrefix + dirSeparator + "User" + dirSeparator;
                }

                Global.SetFilePathPrefix(filePathPrefix);
            }
            catch (Exception e)
            {
                Debug.Console(0, "Unable to Determine Platform due to Exception: {0}", e.Message);
            }
        }

        /// <summary>
        /// Begins the process of loading resources including plugins and configuration data
        /// </summary>
        public void GoWithLoad()
        {
            try
            {
                Debug.SetDoNotLoadConfigOnNextBoot(false);

                PluginLoader.AddProgramAssemblies();

                new Core.DeviceFactory();
                new Devices.Common.DeviceFactory();
                new DeviceFactory();

                new Core.ProcessorExtensionDeviceFactory();

                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials load from configuration");

                var filesReady = SetupFilesystem();
                if (filesReady)
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Checking for plugins");
                    PluginLoader.LoadPlugins();

                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Folder structure verified. Loading config...");
                    if (!ConfigReader.LoadConfig2())
                    {
                        Debug.Console(0, Debug.ErrorLogLevel.Error, "Essentials Load complete with errors");
                        return;
                    }

                    Load();
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Essentials load complete\r\n" +
                                                                 "-------------------------------------------------------------");
                }
                else
                {
                    Debug.Console(0,
                        @"----------------------------------------------
                        ------------------------------------------------
                        ------------------------------------------------
                        Essentials file structure setup completed.
                        Please load config, sgd and ir files and
                        restart program.
                        ------------------------------------------------
                        ------------------------------------------------
                        ------------------------------------------------");
                }

            }
            catch (Exception e)
            {
                Debug.Console(0, "FATAL INITIALIZE ERROR. System is in an inconsistent state:\r\n{0}", e);
            }
            finally
            {
                // Notify the OS that the program intitialization has completed
                SystemMonitor.ProgramInitialization.ProgramInitializationComplete = true;
            }

        }

       

        /// <summary>
        /// Verifies filesystem is set up. IR, SGD, and programX folders
        /// </summary>
        bool SetupFilesystem()
        {
            Debug.Console(0, "Verifying and/or creating folder structure");
            var configDir = Global.FilePathPrefix;

            Debug.Console(0, "FilePathPrefix: {0}", configDir);
            var configExists = Directory.Exists(configDir);
            if (!configExists)
                Directory.Create(configDir);

            var irDir = Global.FilePathPrefix + "ir";
            if (!Directory.Exists(irDir))
                Directory.Create(irDir);

            var sgdDir = Global.FilePathPrefix + "sgd";
			if (!Directory.Exists(sgdDir))
				Directory.Create(sgdDir);

            var pluginDir = Global.FilePathPrefix + "plugins";
            if (!Directory.Exists(pluginDir))
                Directory.Create(pluginDir);

            var joinmapDir = Global.FilePathPrefix + "joinmaps";
            if(!Directory.Exists(joinmapDir))
                Directory.Create(joinmapDir);

			return configExists;
		}

		/// <summary>
		/// 
		/// </summary>
		public void TearDown()
		{
			Debug.Console(0, "Tearing down existing system");
			DeviceManager.DeactivateAll();

			TieLineCollection.Default.Clear();

			foreach (var key in DeviceManager.GetDevices())
				DeviceManager.RemoveDevice(key);

			Debug.Console(0, "Tear down COMPLETE");
		}

		/// <summary>
		/// 
		/// </summary>
		void Load()
		{
			LoadDevices();
			LoadRooms();
			LoadLogoServer();

			DeviceManager.ActivateAll();

            LoadTieLines();

            var mobileControl = GetMobileControlDevice();

		    if (mobileControl == null) return;

            mobileControl.LinkSystemMonitorToAppServer();
		    
		}

        /// <summary>
        /// Reads all devices from config and adds them to DeviceManager
        /// </summary>
        public void LoadDevices()
        {

            // Build the processor wrapper class
            DeviceManager.AddDevice(new PepperDash.Essentials.Core.Devices.CrestronProcessor("processor"));

            // Add global System Monitor device
            if (CrestronEnvironment.DevicePlatform == eDevicePlatform.Appliance)
            {
                DeviceManager.AddDevice(
                    new PepperDash.Essentials.Core.Monitoring.SystemMonitorController("systemMonitor"));
            }

            foreach (var devConf in ConfigReader.ConfigObject.Devices)
            {
                IKeyed newDev = null;

                try
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Creating device '{0}', type '{1}'", devConf.Key, devConf.Type);
                    // Skip this to prevent unnecessary warnings
                    if (devConf.Key == "processor")
                    {
                        var prompt = Global.ControlSystem.ControllerPrompt;

                        var typeMatch = String.Equals(devConf.Type, prompt, StringComparison.OrdinalIgnoreCase) ||
                                        String.Equals(devConf.Type, prompt.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

                        if (!typeMatch)
                            Debug.Console(0,
                                "WARNING: Config file defines processor type as '{0}' but actual processor is '{1}'!  Some ports may not be available",
                                devConf.Type.ToUpper(), Global.ControlSystem.ControllerPrompt.ToUpper());


                        continue;
                    }


                    if (newDev == null)
                        newDev = PepperDash.Essentials.Core.DeviceFactory.GetDevice(devConf);

					if (newDev != null)
						DeviceManager.AddDevice(newDev);
					else
                        Debug.Console(0, Debug.ErrorLogLevel.Error, "ERROR: Cannot load unknown device type '{0}', key '{1}'.", devConf.Type, devConf.Key);
                }
                catch (Exception e)
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Error, "ERROR: Creating device {0}. Skipping device. \r{1}", devConf.Key, e);
                }
            }
            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Devices Loaded.");

        }


        /// <summary>
        /// Helper method to load tie lines.  This should run after devices have loaded
        /// </summary>
        public void LoadTieLines()
        {
            // In the future, we can't necessarily just clear here because devices
            // might be making their own internal sources/tie lines

            var tlc = TieLineCollection.Default;
            //tlc.Clear();
            if (ConfigReader.ConfigObject.TieLines == null)
            {
                return;
            }

            foreach (var tieLineConfig in ConfigReader.ConfigObject.TieLines)
            {
                var newTL = tieLineConfig.GetTieLine();
                if (newTL != null)
                    tlc.Add(newTL);
            }

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Tie Lines Loaded.");

        }

        /// <summary>
        /// Reads all rooms from config and adds them to DeviceManager
        /// </summary>
        public void LoadRooms()
        {
            if (ConfigReader.ConfigObject.Rooms == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Notice: Configuration contains no rooms - Is this intentional?  This may be a valid configuration.");
                return;
            }

            foreach (var roomConfig in ConfigReader.ConfigObject.Rooms)
            {
                var room = Core.DeviceFactory.GetDevice(roomConfig);

                DeviceManager.AddDevice(room);
                if (room is ICustomMobileControl)
                {
                    continue;                    
                }

                BuildMC(room as IEssentialsRoom);
            }

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Rooms Loaded.");

        }

        private static void BuildMC(IEssentialsRoom room)
        {            
            Debug.Console(0, Debug.ErrorLogLevel.Notice, $"Attempting to build Mobile Control Bridge for {room?.Key}");

            CreateMobileControlBridge(room);
        }

        private static void CreateMobileControlBridge(IEssentialsRoom room)
        {
            if(room == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Warning, $"Room does not implement IEssentialsRoom");
                return;
            }

            var mobileControl = GetMobileControlDevice();

            mobileControl?.CreateMobileControlRoomBridge(room, mobileControl);

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "Mobile Control Bridge Added...");
        }

        private static IMobileControl3 GetMobileControlDevice()
        {
            var mobileControlList = DeviceManager.AllDevices.OfType<IMobileControl3>().ToList();

            if (mobileControlList.Count > 1)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Warning,
                    "Multiple instances of Mobile Control Server found.");

                return null;
            }

            if (mobileControlList.Count > 0)
            {
                return mobileControlList[0];
            }

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "Mobile Control not enabled for this system");
            return null;
        }

        /// <summary>
        /// Fires up a logo server if not already running
        /// </summary>
        void LoadLogoServer()
        {
            if (ConfigReader.ConfigObject.Rooms == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "No rooms configured. Bypassing Logo server startup.");
                return;
            }

            if (
                !ConfigReader.ConfigObject.Rooms.Any(
                    CheckRoomConfig))
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "No rooms configured to use system Logo server. Bypassing Logo server startup");
                return;
            }

            try
            {
                LogoServer = new HttpLogoServer(8080, Global.DirectorySeparator + "html" + Global.DirectorySeparator + "logo");
            }
            catch (Exception)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "NOTICE: Logo server cannot be started. Likely already running in another program");
            }
        }

        private bool CheckRoomConfig(DeviceConfig c)
        {
            string logoDark = null;
            string logoLight = null;
            string logo = null;

            try
            {
                if (c.Properties["logoDark"] != null)
                {
                    logoDark = c.Properties["logoDark"].Value<string>("type");
                }

                if (c.Properties["logoLight"] != null)
                {
                    logoLight = c.Properties["logoLight"].Value<string>("type");
                }

                if (c.Properties["logo"] != null)
                {
                    logo = c.Properties["logo"].Value<string>("type");
                }

                return ((logoDark != null && logoDark == "system") ||
                        (logoLight != null && logoLight == "system") || (logo != null && logo == "system"));
            }
            catch
            {
                Debug.Console(1, Debug.ErrorLogLevel.Notice, "Unable to find logo information in any room config");
                return false;
            }
        }
    }
}