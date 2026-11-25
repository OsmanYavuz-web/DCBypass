using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DCBypass;

static class SystemProxy
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static bool _enabledByUs;
    private static int _prevEnable;
    private static string? _prevServer;
    private static bool _savedPrev;

    public static void Enable(int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key == null) return;

            if (!_savedPrev)
            {
                var currentServer = key.GetValue("ProxyServer") as string;
                var currentEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
                if (currentServer == $"127.0.0.1:{port}")
                {
                    _prevEnable = 0;
                    _prevServer = null;
                }
                else
                {
                    _prevEnable = currentEnable;
                    _prevServer = currentServer;
                }
                _savedPrev = true;
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>", RegistryValueKind.String);
            _enabledByUs = true;
            NotifyWindows();
        }
        catch { }
    }

    public static void Restore()
    {
        if (!_enabledByUs) return;
        _enabledByUs = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key == null) return;
            key.SetValue("ProxyEnable", _prevEnable, RegistryValueKind.DWord);
            if (_prevServer != null)
                key.SetValue("ProxyServer", _prevServer, RegistryValueKind.String);
            else
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            NotifyWindows();
        }
        catch { }
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}

sealed class BypassProxyServer
{
    private readonly ConcurrentDictionary<string, IPAddress> _dnsCache = new();
    private readonly HttpClient _dohClient;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private static readonly string[] TargetSuffixes =
    [
        "discord.com",
        "discordapp.com",
        "discordapp.net",
        "discord.gg",
        "discord.media",
        "discordcdn.com",
        "discordstatus.com",
        "dis.gd"
    ];

    public bool IsRunning => _listener != null;
    public int ActivePort { get; private set; }

    public BypassProxyServer()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                await socket.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        _dohClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        _dohClient.DefaultRequestHeaders.Add("Accept", "application/dns-json");
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        ActivePort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        SystemProxy.Enable(ActivePort);
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        ActivePort = 0;
        _cts?.Dispose();
        _cts = null;
        SystemProxy.Restore();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch
            {
                break;
            }
        }
    }

    private static bool NeedsBypass(string host)
    {
        foreach (var suffix in TargetSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task<IPAddress> ResolveHostAsync(string host, bool useDoh)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        if (!useDoh)
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        if (_dnsCache.TryGetValue(host, out var cached))
            return cached;

        try
        {
            var url = $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A";
            var json = await _dohClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Answer", out var answers))
            {
                foreach (var ans in answers.EnumerateArray())
                {
                    if (ans.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                        ans.TryGetProperty("data", out var data) &&
                        IPAddress.TryParse(data.GetString(), out var ip))
                    {
                        _dnsCache[host] = ip;
                        return ip;
                    }
                }
            }
        }
        catch { }

        var fallback = (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _dnsCache[host] = fallback;
        return fallback;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var clientStream = client.GetStream();
                var headerBuf = new byte[8192];
                int read = await clientStream.ReadAsync(headerBuf, ct);
                if (read <= 0) return;

                var headerText = Encoding.ASCII.GetString(headerBuf, 0, read);
                var firstLine = headerText.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;

                if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    var hostPort = parts[1].Split(':');
                    string host = hostPort[0];
                    int port = hostPort.Length > 1 ? int.Parse(hostPort[1]) : 443;

                    bool bypass = NeedsBypass(host);
                    var ip = await ResolveHostAsync(host, useDoh: bypass);

                    using var remote = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    remote.NoDelay = true;
                    await remote.ConnectAsync(ip, port, ct);

                    var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    await clientStream.WriteAsync(ok, ct);

                    using var remoteStream = new NetworkStream(remote, ownsSocket: false);
                    var c2r = bypass
                        ? RelayWithTlsRecordSplitAsync(clientStream, remoteStream, ct)
                        : clientStream.CopyToAsync(remoteStream, ct);
                    var r2c = remoteStream.CopyToAsync(clientStream, ct);
                    await Task.WhenAny(c2r, r2c);
                }
                else
                {
                    var uri = new Uri(parts[1]);
                    bool bypass = NeedsBypass(uri.Host);
                    var ip = await ResolveHostAsync(uri.Host, useDoh: bypass);
                    using var remote = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    remote.NoDelay = true;
                    await remote.ConnectAsync(ip, uri.Port, ct);
                    using var remoteStream = new NetworkStream(remote, ownsSocket: false);
                    await remoteStream.WriteAsync(headerBuf.AsMemory(0, read), ct);
                    await Task.WhenAny(clientStream.CopyToAsync(remoteStream, ct), remoteStream.CopyToAsync(clientStream, ct));
                }
            }
            catch { }
        }
    }

    private static async Task RelayWithTlsRecordSplitAsync(NetworkStream client, NetworkStream remote, CancellationToken ct)
    {
        var buf = new byte[16384];
        int first = await client.ReadAsync(buf, ct);
        if (first <= 0) return;

        var (rec1, rec2, consumed) = TrySplitTlsRecord(buf.AsSpan(0, first));
        if (rec1 != null && rec2 != null)
        {
            await remote.WriteAsync(rec1, ct);
            await remote.FlushAsync(ct);
            await Task.Delay(5, ct);
            await remote.WriteAsync(rec2, ct);
            if (consumed < first)
                await remote.WriteAsync(buf.AsMemory(consumed, first - consumed), ct);
            await remote.FlushAsync(ct);
        }
        else
        {
            await remote.WriteAsync(buf.AsMemory(0, first), ct);
        }

        int n;
        while ((n = await client.ReadAsync(buf, ct)) > 0)
        {
            await remote.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    private static (byte[]? rec1, byte[]? rec2, int consumed) TrySplitTlsRecord(ReadOnlySpan<byte> span)
    {
        if (span.Length <= 10 || span[0] != 0x16 || span[1] != 0x03)
            return (null, null, 0);

        int recordLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(3, 2));
        if (5 + recordLen > span.Length || recordLen <= 4)
            return (null, null, 0);

        int splitAt = 2;
        byte verMajor = span[1];
        byte verMinor = span[2];

        var rec1 = new byte[5 + splitAt];
        rec1[0] = 0x16; rec1[1] = verMajor; rec1[2] = verMinor;
        BinaryPrimitives.WriteUInt16BigEndian(rec1.AsSpan(3, 2), (ushort)splitAt);
        span.Slice(5, splitAt).CopyTo(rec1.AsSpan(5));

        int rem = recordLen - splitAt;
        var rec2 = new byte[5 + rem];
        rec2[0] = 0x16; rec2[1] = verMajor; rec2[2] = verMinor;
        BinaryPrimitives.WriteUInt16BigEndian(rec2.AsSpan(3, 2), (ushort)rem);
        span.Slice(5 + splitAt, rem).CopyTo(rec2.AsSpan(5));

        return (rec1, rec2, 5 + recordLen);
    }
}

sealed class TrayAppContext : ApplicationContext
{
    private readonly BypassProxyServer _server;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _discordItem;
    private readonly Icon _activeIcon;
    private readonly Icon _inactiveIcon;

    public TrayAppContext()
    {
        _server = new BypassProxyServer();
        _activeIcon = CreateCircleIcon(Color.FromArgb(67, 181, 129));
        _inactiveIcon = CreateCircleIcon(Color.FromArgb(140, 140, 140));

        _statusItem = new ToolStripMenuItem("DCBypass: Aktif") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Proxy'yi Durdur", null, (_, _) => ToggleProxy());
        _discordItem = new ToolStripMenuItem("Discord'u Başlat", null, (_, _) => ToggleDiscord());
        var exitItem = new ToolStripMenuItem("Çıkış", null, (_, _) => ExitApp());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_discordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) => UpdateDiscordMenuItem();

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ToggleProxy();

        SystemEvents.SessionEnding += (_, _) => _server.Stop();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _server.Stop();

        try
        {
            _server.Start();
            UpdateUi();
            _trayIcon.ShowBalloonTip(2000, "DCBypass", $"Aktif (127.0.0.1:{_server.ActivePort}). Sağ tıklayıp yönetebilirsiniz.", ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"DCBypass başlatılamadı:\n{ex.Message}", "DCBypass", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
        }
    }

    private void ToggleProxy()
    {
        if (_server.IsRunning)
            _server.Stop();
        else
            _server.Start();
        UpdateUi();
    }

    private void UpdateUi()
    {
        if (_server.IsRunning)
        {
            _trayIcon.Icon = _activeIcon;
            _trayIcon.Text = $"DCBypass: Aktif (Port {_server.ActivePort})";
            _statusItem.Text = $"DCBypass: Aktif (127.0.0.1:{_server.ActivePort})";
            _toggleItem.Text = "Proxy'yi Durdur";
        }
        else
        {
            _trayIcon.Icon = _inactiveIcon;
            _trayIcon.Text = "DCBypass: Kapalı";
            _statusItem.Text = "DCBypass: Kapalı";
            _toggleItem.Text = "Proxy'yi Başlat";
        }
    }

    private static bool IsDiscordRunning() => Process.GetProcessesByName("Discord").Length > 0;

    private void UpdateDiscordMenuItem()
    {
        _discordItem.Text = IsDiscordRunning() ? "Discord'u Kapat" : "Discord'u Başlat";
    }

    private void ToggleDiscord()
    {
        if (IsDiscordRunning())
        {
            foreach (var p in Process.GetProcessesByName("Discord"))
            {
                try { p.Kill(); } catch { }
            }
            return;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var discordDir = Path.Combine(localAppData, "Discord");
            if (!Directory.Exists(discordDir))
            {
                MessageBox.Show("Discord kurulumu bulunamadı.", "DCBypass", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var exe = Directory.GetDirectories(discordDir, "app-*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "Discord.exe"))
                .FirstOrDefault(File.Exists);

            if (exe != null)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
    }

    private void ExitApp()
    {
        _server.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _activeIcon.Dispose();
        _inactiveIcon.Dispose();
        Application.Exit();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateCircleIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 26, 26);
        using var pen = new Pen(Color.White, 2.5f);
        g.DrawEllipse(pen, 3, 3, 26, 26);
        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Local\\DCBypassTrayApp_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("DCBypass zaten çalışıyor (sağ altta sistem tepsisinde).", "DCBypass", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
