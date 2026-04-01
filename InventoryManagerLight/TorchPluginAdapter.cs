 #if TORCH
using System;
using Torch;
using Torch.API;
using Torch.Commands;
using System.Text;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
#if TORCH
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.ModAPI.Interfaces;
using VRage.ModAPI;
using SpaceEngineers.Game.ModAPI;
#endif

namespace InventoryManagerLight
{
    // Plugin class deriving from Torch.TorchPluginBase so Torch's PluginManager can instantiate it.
    public class InventoryManagerPlugin : TorchPluginBase
    {
        private InventoryManager _manager;
        private ITorchBase _torchBase;
        // filesystem command directory for admin one-shot commands (iml_dump, iml_sort_<id>)
        private string _commandDir;
        private DateTime _lastCmdPoll = DateTime.MinValue;
        private readonly int _cmdPollMs = 1000;
        // Action instances held statically so they outlive any single world load.
        private static Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction _sortAction;
        private static bool _actionsRegistered = false;

        // Mod messaging/channel ids
        private const ushort IML_MESSAGE_ID = 60001;
        private const string IML_MOD_CHANNEL = "IML_CHANNEL";

        // Cooldown tracking per-panel
        private static readonly Dictionary<long, DateTime> _lastPressed = new Dictionary<long, DateTime>();

        // Expose plugin instance for command module and action handlers
        private static InventoryManagerPlugin _instance;
        private RuntimeConfig _config;
        private ConfigManager _configManager;
        public static InventoryManagerPlugin Instance => _instance;
        public InventoryManager Manager => _manager;
        public RuntimeConfig Config => _config;
        public ConfigManager ConfigManager => _configManager;

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            try
            {
                _torchBase = torch;
                // resolve plugin directory once — used for both config file and command dir
                string pluginDir = ".";
                try { pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "."; } catch { }
                // load (or create) iml-config.xml before constructing the manager
                _config = new RuntimeConfig();
                _configManager = new ConfigManager(pluginDir, new NLogLogger());
                _configManager.LoadOrCreate(_config);
                _manager = new InventoryManager(_config);
                // tell LcdManager where the plugin folder is (used for snapshot file output)
                LcdManager.Instance.SetPluginDir(pluginDir);
                // prepare command directory under plugin folder for simple admin commands
                try
                {
                    _commandDir = Path.Combine(pluginDir, "iml_cmds");
                    if (!Directory.Exists(_commandDir)) Directory.CreateDirectory(_commandDir);
                }
                catch { _commandDir = null; }
                _instance = this;
                // register Torch console commands (!iml dump / sort / sortall / list)
                try
                {
                    var cmdMgr = torch.Managers?.GetManager(typeof(CommandManager));
                    if (cmdMgr != null)
                    {
                        var mgrType = cmdMgr.GetType();
                        // different Torch versions use different method names
                        var addMethod = mgrType.GetMethod("AddModule", BindingFlags.Public | BindingFlags.Instance)
                                     ?? mgrType.GetMethod("RegisterModule", BindingFlags.Public | BindingFlags.Instance)
                                     ?? mgrType.GetMethod("AddPlugin", BindingFlags.Public | BindingFlags.Instance);
                        if (addMethod != null)
                        {
                            var parms = addMethod.GetParameters();
                            if (parms.Length == 1 && parms[0].ParameterType == typeof(Type))
                                addMethod.Invoke(cmdMgr, new object[] { typeof(ImlCommandModule) });
                            else if (parms.Length == 1)
                                addMethod.Invoke(cmdMgr, new object[] { this });
                        }
                        new NLogLogger().Info("IML: Registered console commands (!iml dump/sort/sortall/list)");
                    }
                }
                catch { }
                RegisterTerminalActions();
                // register message handler for client->server requests
                try { MyAPIGateway.Utilities.RegisterMessageHandler(IML_MESSAGE_ID, OnModMessageReceived); } catch { }
                // log plugin load for visibility in Torch logs
                try { new NLogLogger().Info("InventoryManagerLight plugin initialized"); } catch { }
            }
            catch { }
        }

        public override void Update()
        {
            base.Update();
            // drive the manager pipeline (applier, consumer scanner, sort polling)
            try { _manager?.Tick(); } catch { }
            // lightweight poll for admin command files
            try
            {
                if (!string.IsNullOrEmpty(_commandDir) && (DateTime.UtcNow - _lastCmdPoll).TotalMilliseconds > _cmdPollMs)
                {
                    _lastCmdPoll = DateTime.UtcNow;
                    var files = Directory.GetFiles(_commandDir, "iml_*", SearchOption.TopDirectoryOnly);
                    foreach (var f in files)
                    {
                        try
                        {
                            var nm = Path.GetFileName(f);
                            if (string.Equals(nm, "iml_dump", StringComparison.OrdinalIgnoreCase))
                            {
                                try { _manager?.DumpState(); } catch { }
                            }
                            else if (nm.StartsWith("iml_sort_", StringComparison.OrdinalIgnoreCase))
                            {
                                var sid = nm.Substring("iml_sort_".Length);
                                if (long.TryParse(sid, out var id)) { try { _manager?.TriggerSortForOwner(id); } catch { } }
                            }
                        }
                        catch { }
                        finally
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public override void Dispose()
        {
            try
            {
                try { UnregisterTerminalActions(); } catch { }
                try { MyAPIGateway.Utilities.UnregisterMessageHandler(IML_MESSAGE_ID, OnModMessageReceived); } catch { }
            }
            catch { }
            try { _manager?.Dispose(); } catch { }
            base.Dispose();
        }

        private void RegisterTerminalActions()
        {
            if (_actionsRegistered) return;
            _actionsRegistered = true;
            try
            {
                _sortAction = BuildSortAction("IML_SortNow", "IML: Sort Now");
                MyAPIGateway.TerminalControls.CustomActionGetter += OnCustomActionGetter;
                try { new NLogLogger().Info("IML: Registered terminal action IML_SortNow"); } catch { }
            }
            catch { }
        }

        private void UnregisterTerminalActions()
        {
            try
            {
                MyAPIGateway.TerminalControls.CustomActionGetter -= OnCustomActionGetter;
            }
            catch { }
            _actionsRegistered = false;
        }

        private static Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction BuildSortAction(string id, string label)
        {
            // Register action for all terminal blocks so it shows up in the panel's "Setup actions" picker
            var action = MyAPIGateway.TerminalControls.CreateAction<Sandbox.ModAPI.IMyTerminalBlock>(id);
            action.Name = new System.Text.StringBuilder(label);
            action.ValidForGroups = false;
            action.Enabled = (block) => true;
            action.Action = (block) =>
            {
                try
                {
                    var panel = block as Sandbox.ModAPI.IMyTerminalBlock;
                    if (panel == null) return;
                    // cooldown per-panel
                    long idVal = panel.EntityId;
                    DateTime last;
                    if (_lastPressed.TryGetValue(idVal, out last))
                    {
                        if ((DateTime.UtcNow - last).TotalSeconds < 5.0) return; // 5s cooldown
                    }
                    _lastPressed[idVal] = DateTime.UtcNow;

                    // send message to server if client
                    if (!MyAPIGateway.Session.IsServer)
                    {
                        string msg = "IML_SORT|" + panel.EntityId;
                        byte[] data = Encoding.UTF8.GetBytes(msg);
                        MyAPIGateway.Multiplayer.SendMessageToServer(IML_MESSAGE_ID, data);
                    }
                    else
                    {
                        // server: directly trigger
                        try { _instance?._manager?.TriggerSortForOwner(panel.EntityId); } catch { }
                    }
                }
                catch { }
            };
            action.Writer = (block, sb) => sb.Append(label);
            return action;
        }

        private static void OnModMessageReceived(object obj)
        {
            try
            {
                if (!(obj is byte[] data)) return;
                var s = Encoding.UTF8.GetString(data);
                if (s.StartsWith("IML_SORT|"))
                {
                    var parts = s.Split(new[] { '|' }, 2);
                    if (parts.Length == 2 && long.TryParse(parts[1], out var id))
                    {
                        try { _instance?._manager?.TriggerSortForOwner(id); } catch { }
                    }
                }
            }
            catch { }
        }

        private static void OnCustomActionGetter(Sandbox.ModAPI.IMyTerminalBlock block, List<Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction> actions)
        {
            // Inject our action for any terminal block (button panels will see it in "Setup actions")
            try
            {
                if (_sortAction != null)
                    actions.Add(_sortAction);
            }
            catch { }
        }
    }
}
#endif
