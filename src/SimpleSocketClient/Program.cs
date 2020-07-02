using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleSocketClient
{
    class Program
    {
        static bool _isConnect = false;
        static readonly ConcurrentQueue<string> _messages = new ConcurrentQueue<string>();

        static void Main(string[] args)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();
            Task ui = controlUIAsync(cancellation);
            Task server = connectServerAsync(cancellation.Token);
            Task.WaitAll(ui, server);
        }

        static async Task controlUIAsync(CancellationTokenSource cancellation)
        {
            while (!_isConnect)
            {
                Console.WriteLine("Connecting to server...");
                await Task.Delay(500, cancellation.Token);
            }

            Console.WriteLine("The server is connected.");
            Console.WriteLine("What's your Name?");

            string name = Console.ReadLine();
            Console.WriteLine("Hello {0} , When you want to leave process you can type \"exit\" to leave.", name);

            while (!cancellation.Token.IsCancellationRequested)
            {
                string message = Console.ReadLine();
                if (message == "exit")
                {
                    cancellation.Cancel();
                    break;
                }

                _messages.Enqueue($"{name}: {message}");
            }
        }

        static async Task connectServerAsync(CancellationToken stoppingToken)
        {
            TcpClient client = new TcpClient(Dns.GetHostName(), 8700);
            using (var stream = client.GetStream())
            {
                _isConnect = true;

                Task sender = Task.Factory.StartNew(() =>
                    processPendingMessageAsync(stream, stoppingToken),
                    TaskCreationOptions.LongRunning);
                Task receiver = Task.Factory.StartNew(() =>
                     processReceiveMessageAsync(stream, stoppingToken),
                    TaskCreationOptions.LongRunning);
                await Task.WhenAll(sender, receiver);
            }
        }

        static async Task processReceiveMessageAsync(NetworkStream stream, CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SpinWait.SpinUntil(() => stoppingToken.IsCancellationRequested || stream.DataAvailable);
                var sr = new StreamReader(stream);
                string message = await sr.ReadLineAsync();
                Console.WriteLine(message);
            }
        }

        static async Task processPendingMessageAsync(NetworkStream stream, CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string message = null;
                SpinWait.SpinUntil(() => stoppingToken.IsCancellationRequested || _messages.TryDequeue(out message));
                if (!string.IsNullOrWhiteSpace(message))
                {
                    var sw = new StreamWriter(stream);
                    await sw.WriteLineAsync(message);
                    await sw.FlushAsync();
                }
            }
        }
    }
}
