using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleSocketServer
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly ConcurrentDictionary<string, TcpClient> _clients;
        private readonly ConcurrentQueue<string> _messages;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            _clients = new ConcurrentDictionary<string, TcpClient>();
            _messages = new ConcurrentQueue<string>();
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Task server = startTcpServerAsync(stoppingToken);
            Task broadcaster = startBroadcastAsync(stoppingToken);
            Task monitor = startMonitorAsync(stoppingToken);
            return Task.WhenAll(server, broadcaster, monitor);
        }

        private async Task startTcpServerAsync(CancellationToken stoppingToken)
        {
            var tasks = new ConcurrentBag<Task>();
            var tcpListener = new TcpListener(IPAddress.Any, 8700);

            _logger.LogInformation("Start socket server: {0}", tcpListener.LocalEndpoint.ToString());
            tcpListener.Start();
            using (stoppingToken.Register(() => tcpListener.Stop()))
            {
                try
                {
                    tcpListener.Server.IOControl(IOControlCode.KeepAliveValues, getKeepAlive(), null);
                    tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var tcpClient = await tcpListener.AcceptTcpClientAsync();
                        tasks.Add(Task.Factory.StartNew(() =>
                            processConnectionAsync(tcpClient, stoppingToken),
                            TaskCreationOptions.LongRunning));
                    }
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("The process will be shutdown.");
                }
                finally
                {
                    foreach (var client in _clients)
                    {
                        releaseConnection(client.Key, client.Value);
                    }
                    _clients.Clear();
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }
            _logger.LogInformation("End socket server.");
        }

        private async Task startBroadcastAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_messages.TryDequeue(out string message))
                {
                    _logger.LogInformation("Send message to {0} clients.", _clients.Count);
                    foreach (var client in _clients)
                    {
                        try
                        {
                            var sw = new StreamWriter(client.Value.GetStream());
                            await sw.WriteLineAsync(message);
                            await sw.FlushAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Send a message to {0} failed.", client.Key);
                        }
                    }
                }
                await Task.Delay(100, stoppingToken);
            }
        }

        private async Task processConnectionAsync(TcpClient client, CancellationToken stoppingToken)
        {
            string remoteEndPoint = client.Client.RemoteEndPoint.ToString();
            _clients.TryAdd(remoteEndPoint, client);
            _logger.LogInformation("New connection: {0}", remoteEndPoint);

            try
            {
                client.Client.IOControl(IOControlCode.KeepAliveValues, getKeepAlive(), null);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                using (var stream = client.GetStream())
                {
                    while (!stoppingToken.IsCancellationRequested || !client.Connected)
                    {
                        SpinWait.SpinUntil(() => stoppingToken.IsCancellationRequested || !client.Connected || stream.DataAvailable);
                        if (stoppingToken.IsCancellationRequested || !client.Connected)
                        {
                            _logger.LogInformation("The connection {0} will be close.", remoteEndPoint);
                            break;
                        }
                        _logger.LogInformation("Received message from {0}", remoteEndPoint);

                        var sr = new StreamReader(stream);
                        string message = await sr.ReadLineAsync();
                        _messages.Enqueue(message);
                    }
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "The connection is failed and it will be removed.");
            }
            finally
            {
                releaseConnection(remoteEndPoint, client);
            }
        }

        private void releaseConnection(string remoteEndPoint, TcpClient client)
        {
            try
            {
                client.Close();
                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Release {0} connection failed.", remoteEndPoint);
            }
            finally
            {
                _clients.Remove(remoteEndPoint, out TcpClient _);
            }
        }

        private byte[] getKeepAlive(int onOff = 1, int keepAliveTime = 3000, int keepAliveInterval = 1000)
        {
            byte[] buffer = new byte[12];
            BitConverter.GetBytes(onOff).CopyTo(buffer, 0);
            BitConverter.GetBytes(keepAliveTime).CopyTo(buffer, 4);
            BitConverter.GetBytes(keepAliveInterval).CopyTo(buffer, 8);
            return buffer;
        }

        private async Task startMonitorAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
                TcpConnectionInformation[] tcpConnections = ipProperties.GetActiveTcpConnections();
                foreach (var client in _clients)
                {
                    if (!isClientConnected(tcpConnections, client.Value))
                    {
                        _logger.LogWarning("The client {0} is disconnected.", client.Key);
                        releaseConnection(client.Key, client.Value);
                    }
                }
                await Task.Delay(1000 * 10, stoppingToken);
            }
        }

        private bool isClientConnected(TcpConnectionInformation[] tcpConnections, TcpClient client)
        {
            try
            {
                foreach (TcpConnectionInformation c in tcpConnections)
                {
                    if (c.LocalEndPoint.Equals(client.Client.LocalEndPoint) && 
                        c.RemoteEndPoint.Equals(client.Client.RemoteEndPoint))
                    {
                        TcpState stateOfConnection = c.State;
                        return stateOfConnection == TcpState.Established;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception.");
            }
            return false;
        }
    }
}
