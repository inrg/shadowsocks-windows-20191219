﻿using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Shadowsocks.Model;
using Shadowsocks.Util.ProcessManagement;

namespace Shadowsocks.Controller.Service
{
    // https://github.com/shadowsocks/shadowsocks-org/wiki/Plugin
    public sealed class Sip003Plugin : IDisposable
    {
        public IPEndPoint LocalEndPoint { get; private set; }
        public int ProcessId => _started ? _pluginProcess.Id : 0;

        private readonly object _startProcessLock = new object();
        private readonly Job _pluginJob;
        private readonly Process _pluginProcess;
        private bool _started;
        private bool _disposed;

        public static Sip003Plugin CreateIfConfigured(Server server, bool showPluginOutput)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            if (string.IsNullOrWhiteSpace(server.plugin))
            {
                return null;
            }

            return new Sip003Plugin(
                server.plugin,
                server.plugin_opts,
                server.plugin_args,
                server.server,
                server.server_port,
                showPluginOutput);
        }

        private Sip003Plugin(string plugin, string pluginOpts, string pluginArgs, string serverAddress, int serverPort, bool showPluginOutput)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(serverAddress));
            }
            if (serverPort <= 0 || serverPort > 65535)
            {
                throw new ArgumentOutOfRangeException("serverPort");
            }

            var appPath = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().GetName().CodeBase).LocalPath);

            _pluginProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = plugin,
                    Arguments = pluginArgs,
                    UseShellExecute = false,
                    CreateNoWindow = !showPluginOutput,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = appPath ?? Environment.CurrentDirectory,
                    Environment =
                    {
                        ["SS_REMOTE_HOST"] = serverAddress,
                        ["SS_REMOTE_PORT"] = serverPort.ToString(),
                        ["SS_PLUGIN_OPTIONS"] = pluginOpts
                    }
                }
            };

            _pluginJob = new Job();
        }

        public bool StartIfNeeded()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            lock (_startProcessLock)
            {
                if (_started && !_pluginProcess.HasExited)
                {
                    return false;
                }

                var localPort = GetNextFreeTcpPort();
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);

                _pluginProcess.StartInfo.Environment["SS_LOCAL_HOST"] = LocalEndPoint.Address.ToString();
                _pluginProcess.StartInfo.Environment["SS_LOCAL_PORT"] = LocalEndPoint.Port.ToString();
                _pluginProcess.StartInfo.Arguments = ExpandEnvironmentVariables(_pluginProcess.StartInfo.Arguments, _pluginProcess.StartInfo.EnvironmentVariables);
                _pluginProcess.Start();
                _pluginJob.AddProcess(_pluginProcess.Handle);
                _started = true;
            }

            return true;
        }

        public string ExpandEnvironmentVariables(string name, StringDictionary environmentVariables = null)
        {
            // Expand the environment variables from the new process itself
            if (environmentVariables != null)
            {
                foreach(string key in environmentVariables.Keys)
                {
                    name = name.Replace($"%{key}%", environmentVariables[key], StringComparison.OrdinalIgnoreCase);
                }
            }
            // Also expand the environment variables from current main process (system)
            name = Environment.ExpandEnvironmentVariables(name);
            return name;
        }

        static int GetNextFreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_pluginProcess.HasExited)
                {
                    _pluginProcess.Kill();
                    _pluginProcess.WaitForExit();
                }
            }
            catch (Exception) { }
            finally
            {
                try
                {
                    _pluginProcess.Dispose();
                    _pluginJob.Dispose();
                }
                catch (Exception) { }

                _disposed = true;
            }
        }
    }
}