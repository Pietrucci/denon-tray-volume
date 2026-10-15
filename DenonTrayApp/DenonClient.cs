using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DenonTrayApp
{
    public class DenonClient
    {
        private readonly string _ip;
        private readonly int _port;
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;

        public event Action<int>? VolumeChanged;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<string>? SoundModeChanged;

        public bool IsConnected => _client?.Connected ?? false;

        public DenonClient(string ip = "192.168.1.42", int port = 23)
        {
            _ip = ip;
            _port = port;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectionLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            Disconnect();
        }

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_client == null || !_client.Connected)
                    {
                        ConnectionStateChanged?.Invoke(false);
                        _client = new TcpClient();

                        // Łączenie z timeoutem
                        var connectTask = _client.ConnectAsync(_ip, _port, token).AsTask();
                        if (await Task.WhenAny(connectTask, Task.Delay(3000, token)) != connectTask)
                            throw new TimeoutException("Timeout połączenia");

                        var stream = _client.GetStream();
                        _reader = new StreamReader(stream, Encoding.ASCII);
                        _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                        ConnectionStateChanged?.Invoke(true);

                        // Od razu po połączeniu pytamy o aktualną głośność i tryb dźwięku
                        await _writer.WriteAsync("MV?\r");
                        await _writer.FlushAsync();
                        await _writer.WriteAsync("MS?\r");
                        await _writer.FlushAsync();
                    }

                    // Nasłuchiwanie na przychodzące zdarzenia z Denona (np. z pilota)
                    string? line = await _reader.ReadLineAsync(token);
                    if (line != null)
                    {
                        ParseMessage(line);
                    }
                    else
                    {
                        // Połączenie zerwane przez serwer
                        Disconnect();
                    }
                }
                catch
                {
                    Disconnect();
                    // Czekaj 3 sekundy przed kolejną próbą połączenia
                    await Task.Delay(3000, token);
                }
            }
        }

        private void ParseMessage(string line)
        {
            // Odpowiedzi i powiadomienia głośności: "MV50", "MV505", itp.
            if (line.StartsWith("MV") && !line.StartsWith("MVMAX"))
            {
                string volStr = line.Substring(2).Trim();
                if (volStr.Length == 2 && int.TryParse(volStr, out int v2))
                {
                    VolumeChanged?.Invoke(v2);
                }
                else if (volStr.Length == 3 && int.TryParse(volStr.Substring(0, 2), out int v3))
                {
                    VolumeChanged?.Invoke(v3);
                }
            }

            // Sound mode notifications start with "MS" (e.g. "MSSTEREO", "MSDOLBY SURROUND")
            if (line.StartsWith("MS"))
            {
                var mode = line.Substring(2).Trim();
                SoundModeChanged?.Invoke(mode);
            }
        }

        public async Task SetVolumeAsync(double volume)
        {
            if (_writer != null && IsConnected)
            {
                try
                {
                    int whole = (int)Math.Floor(volume);
                    bool half = Math.Abs(volume - whole - 0.5) < 0.001;
                    string cmd;
                    if (half)
                    {
                        if (whole < 10)
                            cmd = $"MV0{whole}5";
                        else
                            cmd = $"MV{whole}5";
                    }
                    else
                    {
                        if (whole < 10)
                            cmd = $"MV0{whole}";
                        else
                            cmd = $"MV{whole}";
                    }

                    await _writer.WriteAsync(cmd + "\r");
                    await _writer.FlushAsync();
                }
                catch { }
            }
        }

        // Send an arbitrary raw command string to the Denon over the telnet socket.
        // Caller is responsible for using the correct device-specific command format.
        public async Task<bool> SendRawAsync(string command)
        {
            if (_writer != null && IsConnected)
            {
                try
                {
                    await _writer.WriteAsync(command + "\r");
                    await _writer.FlushAsync();
                    return true;
                }
                catch { }
            }

            return false;
        }

        private void Disconnect()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            _client = null;
            _reader = null;
            _writer = null;
        }
    }
}