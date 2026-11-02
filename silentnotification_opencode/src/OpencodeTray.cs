using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Opencode Tray - icon in the Windows notification area (dropdown menu).
// States: Idle (black) / Working (green) / Attention (yellow + warning).
// Compiled with csc.exe (.NET Framework 4.x): no external dependencies.
namespace OpencodeTray
{
    internal enum TrayState
    {
        Idle = 0,
        Working = 1,
        Attention = 2
    }

    internal sealed class StateUpdate
    {
        public string State;
        public string Reason;
        public string Pid;
        public string Project;
        public string Cwd;
        public string Sessions;
        public double Cost = double.NaN;
        public string ModelsLine;
    }

    // Riga "provider|model|pricedAs|input|output|reasoning|cacheRead|cacheWrite|cost"
    // sent by plugin for each model used (token estimate x price list).
    internal sealed class ModelStat
    {
        public string Provider = string.Empty;
        public string Model = string.Empty;
        public string PricedAs = string.Empty;
        public long Input, Output, Reasoning, CacheRead, CacheWrite;
        public double Cost;

        public string Key { get { return Provider + "/" + Model; } }

        public ModelStat Clone()
        {
            return (ModelStat)MemberwiseClone();
        }

        public void Add(ModelStat d)
        {
            Input += d.Input;
            Output += d.Output;
            Reasoning += d.Reasoning;
            CacheRead += d.CacheRead;
            CacheWrite += d.CacheWrite;
            Cost += d.Cost;
        }

        // Difference between cumulative received now and previous one.
        // If cumulative decreased (plugin restarted from zero) everything
        // is considered new: so total never goes backward.
        public static ModelStat Delta(ModelStat cur, ModelStat last)
        {
            if (last == null) return cur.Clone();
            bool reset =
                cur.Cost < last.Cost - 1e-9 ||
                cur.Input < last.Input ||
                cur.Output < last.Output ||
                cur.Reasoning < last.Reasoning ||
                cur.CacheRead < last.CacheRead ||
                cur.CacheWrite < last.CacheWrite;
            ModelStat d = new ModelStat();
            d.Provider = cur.Provider;
            d.Model = cur.Model;
            d.PricedAs = cur.PricedAs;
            if (reset) return cur.Clone();
            d.Input = cur.Input - last.Input;
            d.Output = cur.Output - last.Output;
            d.Reasoning = cur.Reasoning - last.Reasoning;
            d.CacheRead = cur.CacheRead - last.CacheRead;
            d.CacheWrite = cur.CacheWrite - last.CacheWrite;
            d.Cost = cur.Cost - last.Cost;
            return d;
        }
    }

    // Data for an opencode session (one protocol line).
    internal sealed class SessionUpdate
    {
        public string Sid = string.Empty;
        public string Project = string.Empty;
        public string Path = string.Empty;
        public string State = string.Empty;
        public string Reason = string.Empty;
        public string Activity = string.Empty;
        public double Cost24h;
        public double Cost7d;
        public long FirstAt;
        public Dictionary<string, ModelStat> Models = new Dictionary<string, ModelStat>();
    }

    // Opencode session known to tray: received cumulatives, accumulated deltas.
    internal sealed class SessionStat
    {
        public string Sid = string.Empty;
        public string Project = string.Empty;
        public string Path = string.Empty;
        public string Activity = string.Empty;
        public string Reason = string.Empty;
        public TrayState State = TrayState.Idle;
        public long LastSeen;
        public long FirstAt;
        public double Cost24h;
        public double Cost7d;
        public bool Live;

        public Dictionary<string, ModelStat> Models = new Dictionary<string, ModelStat>();
        public Dictionary<string, ModelStat> Last = new Dictionary<string, ModelStat>();
        public Dictionary<string, ModelStat> Totals = new Dictionary<string, ModelStat>();
    }

    // "Instance" = project (work folder) on which opencode runs.
    internal sealed class Instance
    {
        public string Path = string.Empty;
        public string Name = string.Empty;
        public long LastSeen;
        public bool Live;
        public TrayState State = TrayState.Idle;
        public string Reason = string.Empty;
        public string Activity = string.Empty;

        public Dictionary<string, SessionStat> Sessions = new Dictionary<string, SessionStat>();
        // accumulated costs per model (sum of received deltas)
        public Dictionary<string, ModelStat> Totals = new Dictionary<string, ModelStat>();
        // baseline fixed by "Reset cost count": menu shows received - base
        public Dictionary<string, ModelStat> Base = new Dictionary<string, ModelStat>();

        public ModelStat TotalOf(string key)
        {
            ModelStat t;
            if (!Totals.TryGetValue(key, out t)) return null;
            return t;
        }

        public void Accumulate(string key, ModelStat delta)
        {
            ModelStat t;
            if (!Totals.TryGetValue(key, out t))
            {
                t = new ModelStat();
                t.Provider = delta.Provider;
                t.Model = delta.Model;
                t.PricedAs = delta.PricedAs;
                Totals[key] = t;
            }
            t.Add(delta);
        }

        // Costo mostrato = accumulato - baseline (mai negativo).
        public double DisplayCost()
        {
            double sum = 0.0;
            foreach (KeyValuePair<string, ModelStat> kv in Totals)
            {
                ModelStat b;
                double baseCost = Base.TryGetValue(kv.Key, out b) ? b.Cost : 0.0;
                double v = kv.Value.Cost - baseCost;
                if (v > 0) sum += v;
            }
            return sum;
        }

        public long DisplayLong(string key, string field)
        {
            ModelStat t;
            if (!Totals.TryGetValue(key, out t)) return 0;
            ModelStat b;
            long baseVal = 0;
            if (Base.TryGetValue(key, out b))
            {
                if (field == "input") baseVal = b.Input;
                else if (field == "output") baseVal = b.Output;
                else if (field == "reasoning") baseVal = b.Reasoning;
                else if (field == "cacheRead") baseVal = b.CacheRead;
                else if (field == "cacheWrite") baseVal = b.CacheWrite;
            }
            long v = 0;
            if (field == "input") v = t.Input;
            else if (field == "output") v = t.Output;
            else if (field == "reasoning") v = t.Reasoning;
            else if (field == "cacheRead") v = t.CacheRead;
            else if (field == "cacheWrite") v = t.CacheWrite;
            long r = v - baseVal;
            return r > 0 ? r : 0;
        }
    }

    internal sealed class Settings
    {
        public int Port = 47110;
        // Stealth di default: nessun popup, nessun suono, nessuna animazione.
        // The signal is only the icon color. Other options are activated from the menu.
        public bool Balloon = false;
        public bool Sound = false;
        public bool Flash = false;
        public int HeartbeatTimeoutSec = 25;
        public bool StartWithWindows = false;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode-tray");
                return Path.Combine(dir, "settings.json");
            }
        }

        public static string ConfigDir
        {
            get
            {
                try { return Path.GetDirectoryName(FilePath); }
                catch { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                int n;
                string v = Json.Read(json, "port");
                if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0 && n < 65536) s.Port = n;
                s.Balloon = Json.ReadBool(json, "balloon", s.Balloon);
                s.Sound = Json.ReadBool(json, "sound", s.Sound);
                s.Flash = Json.ReadBool(json, "flash", s.Flash);
                s.StartWithWindows = Json.ReadBool(json, "startWithWindows", s.StartWithWindows);
                v = Json.Read(json, "heartbeatTimeoutSec");
                if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 5) s.HeartbeatTimeoutSec = n;
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"port\": ").Append(Port.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"balloon\": ").Append(Balloon ? "true" : "false").Append(",\n");
                sb.Append("  \"sound\": ").Append(Sound ? "true" : "false").Append(",\n");
                sb.Append("  \"flash\": ").Append(Flash ? "true" : "false").Append(",\n");
                sb.Append("  \"heartbeatTimeoutSec\": ").Append(HeartbeatTimeoutSec.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"startWithWindows\": ").Append(StartWithWindows ? "true" : "false").Append("\n");
                sb.Append("}\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal static class Json
    {
        public static string Read(string json, string key)
        {
            if (json == null) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i += needle.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;
            if (json[i] == '"') return ReadString(json, i);
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']') i++;
            return json.Substring(start, i - start).Trim();
        }

        private static string ReadString(string json, int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\' && i < json.Length)
                {
                    char e = json[i++];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'u' && i + 4 <= json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append(e);
                }
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool ReadBool(string json, string key, bool fallback)
        {
            string v = Read(json, key);
            if (v == null) return fallback;
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static double ReadDouble(string json, string key, double fallback)
        {
            string v = Read(json, key);
            if (v == null) return fallback;
            double d;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }
    }

    // Logo: una "O" (anello) con dentro un carattere strano che ricorda un quadrato.
    internal static class LogoFactory
    {
        public static readonly Color IdleColor = ColorTranslator.FromHtml("#15181C");
        public static readonly Color WorkingColor = ColorTranslator.FromHtml("#22C55E");
        public static readonly Color AttentionColor = ColorTranslator.FromHtml("#FFC107");
        public static readonly Color DimColor = ColorTranslator.FromHtml("#6B7280");

        private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 48, 256 };

        public static Bitmap Render(Color color, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (Brush brush = new SolidBrush(color))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float s = size;
                float pad = s * 0.055f;
                float d = s - 2f * pad;
                float ring = Math.Max(1.4f, s * 0.115f);

                using (Pen pen = new Pen(color, ring))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawEllipse(pen, pad, pad, d, d);
                }

                // quadrato "strano": contorno aperto + punto staccato
                float sq = s * 0.34f;
                float x0 = (s - sq) / 2f;
                float x1 = x0 + sq;
                float y0 = x0;
                float y1 = x0 + sq;
                float dot = Math.Max(1f, s * 0.085f);

                if (size <= 20)
                {
                    float inset = sq * 0.24f;
                    g.FillRectangle(brush, x0 + inset, y0 + inset, sq - 2f * inset, sq - 2f * inset);
                    g.FillRectangle(brush, x1 - dot, y1 - dot, dot, dot);
                }
                else
                {
                    float thin = Math.Max(1f, s * 0.075f);
                    using (Pen sp = new Pen(color, thin))
                    {
                        sp.StartCap = LineCap.Round;
                        sp.EndCap = LineCap.Round;
                        sp.LineJoin = LineJoin.Round;
                        g.DrawLines(sp, new PointF[] {
                            new PointF(x1, y0 + sq * 0.18f),
                            new PointF(x1, y1),
                            new PointF(x0, y1),
                            new PointF(x0, y0),
                            new PointF(x1 - sq * 0.18f, y0)
                        });
                    }
                    g.FillRectangle(brush, x1 - dot * 1.6f, y0, dot, dot);
                }
            }
            return bmp;
        }

        public static byte[] BuildIco(Color color)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (int size in Sizes) images.Add(ToIconImage(Render(color, size), size));

            int offset = 6 + 16 * Sizes.Length;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write((ushort)0);
                    w.Write((ushort)1);
                    w.Write((ushort)Sizes.Length);
                    for (int k = 0; k < Sizes.Length; k++)
                    {
                        int size = Sizes[k];
                        byte dim = (byte)(size >= 256 ? 0 : size);
                        w.Write(dim);
                        w.Write(dim);
                        w.Write((byte)0);
                        w.Write((byte)0);
                        w.Write((ushort)1);
                        w.Write((ushort)32);
                        w.Write(images[k].Length);
                        w.Write(offset);
                        offset += images[k].Length;
                    }
                    foreach (byte[] img in images) w.Write(img);
                    w.Flush();
                }
                return ms.ToArray();
            }
        }

        public static Icon Create(Color color)
        {
            // Icon(Stream, Size) carica il .ico multi-risoluzione in memoria: nessun
            // problema di stream aperto, e la tray sceglie la taglia giusta al DPI.
            byte[] bytes = BuildIco(color);
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                return new Icon(ms, new Size(32, 32));
            }
        }

        private static byte[] ToIconImage(Bitmap bmp, int size)
        {
            int stride = size * 4;
            byte[] topDown = new byte[stride * size];
            byte[] bottomUp = new byte[stride * size];

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] raw = new byte[data.Stride * size];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                for (int y = 0; y < size; y++)
                    Buffer.BlockCopy(raw, y * data.Stride, topDown, y * stride, stride);
            }
            finally
            {
                bmp.UnlockBits(data);
                bmp.Dispose();
            }
            for (int y = 0; y < size; y++)
                Buffer.BlockCopy(topDown, y * stride, bottomUp, (size - 1 - y) * stride, stride);

            int maskStride = ((size + 31) / 32) * 4;
            byte[] mask = new byte[maskStride * size];

            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(40);
                    w.Write(size);
                    w.Write(size * 2);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(0);
                    w.Write(bottomUp.Length + mask.Length);
                    w.Write(0);
                    w.Write(0);
                    w.Write(0);
                    w.Write(0);
                    w.Write(bottomUp);
                    w.Write(mask);
                    w.Flush();
                }
                return ms.ToArray();
            }
        }
    }

    // Server TCP minimale su 127.0.0.1 (niente HttpListener -> nessun URL ACL, nessun admin).
    internal sealed class StateServer : IDisposable
    {
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly int _port;

        public event Action<StateUpdate> OnState;
        public event Action<string> OnLog;

        public StateServer(int port) { _port = port; }

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
            }
            catch { return false; }
            _running = true;
            _thread = new Thread(AcceptLoop);
            _thread.IsBackground = true;
            _thread.Name = "opencode-tray-server";
            _thread.Start();
            return true;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }
                try { ThreadPool.QueueUserWorkItem(HandleClient, client); }
                catch { try { client.Close(); } catch { } }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 4000;
                using (NetworkStream ns = client.GetStream())
                {
                    byte[] buffer = new byte[65536];
                    int len = 0;
                    string head = string.Empty;
                    int sep = -1;
                    while (len < buffer.Length)
                    {
                        int n = ns.Read(buffer, len, buffer.Length - len);
                        if (n <= 0) break;
                        len += n;
                        head = Encoding.UTF8.GetString(buffer, 0, len);
                        sep = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (sep >= 0) break;
                    }

                    string body = string.Empty;
                    if (sep >= 0)
                    {
                        int cl = 0;
                        string contentLength = FindHeader(head, "content-length");
                        if (contentLength != null)
                            int.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out cl);
                        while (len - (sep + 4) < cl)
                        {
                            int need = cl - (len - (sep + 4));
                            int m = ns.Read(buffer, len, need);
                            if (m <= 0) break;
                            len += m;
                        }
                        int bodyLen = Math.Max(0, Math.Min(cl, len - (sep + 4)));
                        body = Encoding.UTF8.GetString(buffer, sep + 4, bodyLen);
                        head = head.Substring(0, sep + 4);
                    }

                    string reply = Dispatch(head, body);
                    if (OnLog != null && !reply.StartsWith("ok ", StringComparison.Ordinal))
                        OnLog("http: " + head.Split('\n')[0].Trim() + " | " + reply);
                    Respond(ns, reply);
                }
            }
            catch (Exception ex)
            {
                if (OnLog != null) OnLog("errore server: " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private string Dispatch(string head, string body)
        {
            if (head.TrimStart().StartsWith("GET", StringComparison.OrdinalIgnoreCase)) return "ok";
            if (string.IsNullOrEmpty(body)) return "corpo vuoto";
            try
            {
                StateUpdate u = new StateUpdate();
                u.State = Json.Read(body, "state");
                u.Reason = Json.Read(body, "reason");
                u.Pid = Json.Read(body, "pid");
                u.Project = Json.Read(body, "project");
                u.Cwd = Json.Read(body, "cwd");
                u.Cost = Json.ReadDouble(body, "cost", double.NaN);
                u.ModelsLine = Json.Read(body, "models");
                u.Sessions = Json.Read(body, "sessions");
                if (string.IsNullOrEmpty(u.State)) return "stato vuoto";
                if (OnState != null) OnState(u);
                return "ok " + u.State;
            }
            catch (Exception ex)
            {
                return "errore " + ex.GetType().Name;
            }
        }

        private static void Respond(NetworkStream ns, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private static string FindHeader(string head, string name)
        {
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;
                if (lines[i].Substring(0, c).Trim().ToLowerInvariant() == name)
                    return lines[i].Substring(c + 1).Trim();
            }
            return null;
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Settings _settings;
        private NotifyIcon _notify;
        private StateServer _server;
        private System.Windows.Forms.Timer _timer;
        private readonly Dictionary<string, Instance> _instances = new Dictionary<string, Instance>();
        // basi di conteggio salvate su disco (non si perdono al riavvio dell'app)
        private readonly Dictionary<string, Dictionary<string, ModelStat>> _baseStore = new Dictionary<string, Dictionary<string, ModelStat>>();
        private static readonly string LogFile = Path.Combine(Settings.ConfigDir, "tray.log");
        private static readonly string BaseFile = Path.Combine(Settings.ConfigDir, "cost-base.txt");

        private TrayState _state = TrayState.Idle;
        private string _reason = string.Empty;
        private Icon _iconIdle, _iconWorking, _iconAttention, _iconDim;
        private int _flashLeft;
        private bool _flashOn = true;
        private int _testUntil;
        private volatile bool _uiDirty = true;

        private ToolStripMenuItem _statusItem, _costItem, _resetCostItem, _costInfoItem, _projectsItem,
            _balloonItem, _soundItem, _flashItem, _autostartItem;
        private TrayMessageWindow _messageWindow;

        private const string StartupLinkName = "OpencodeTray.lnk";
        private const int REPORT_WIDTH = 74;

        public TrayApp(string[] args)
        {
            _settings = Settings.Load();
            LoadBases();
            _iconIdle = LogoFactory.Create(LogoFactory.IdleColor);
            _iconWorking = LogoFactory.Create(LogoFactory.WorkingColor);
            _iconAttention = LogoFactory.Create(LogoFactory.AttentionColor);
            _iconDim = LogoFactory.Create(LogoFactory.DimColor);

            WriteIconFile();

            _notify = new NotifyIcon();
            _notify.Icon = _iconIdle;
            _notify.Text = "opencode - inattivo";
            _notify.Visible = true;
            _notify.MouseDoubleClick += OnDoubleClick;

            BuildMenu();

            _server = new StateServer(_settings.Port);
            _server.OnState += OnStateReceived;
            _server.OnLog += Log;
            if (!_server.Start())
            {
                Log("ERRORE: porta " + _settings.Port + " occupata, nessun'altra istanza attiva?");
                _notify.Text = "opencode tray: porta " + _settings.Port + " occupata";
            }
            else
            {
                Log("in ascolto su 127.0.0.1:" + _settings.Port);
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            HookExplorer();
            UpdateIcon();

            // --test-attention | --test-working | --test-idle : utile per verificare
            // popup e icone senza dover attendere una sessione opencode.
            foreach (string arg in args)
            {
                TrayState test;
                if (arg == "--test-attention") test = TrayState.Attention;
                else if (arg == "--test-working") test = TrayState.Working;
                else if (arg == "--test-idle") test = TrayState.Idle;
                else continue;
                System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
                once.Interval = 1500;
                TrayState target = test;
                once.Tick += delegate
                {
                    once.Stop();
                    once.Dispose();
                    ApplyTest(target);
                };
                once.Start();
            }

            // --show-costs : apre subito la finestra della stima costi
            // --show-menu  : opens icon menu immediately
            foreach (string arg in args)
            {
                if (arg != "--show-costs" && arg != "--show-menu") continue;
                System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
                once.Interval = 2500;
                once.Tick += delegate
                {
                    once.Stop();
                    once.Dispose();
                    if (arg == "--show-costs") ShowCostInfo();
                    else
                    {
                        ContextMenuStrip menu = _notify.ContextMenuStrip;
                        if (menu != null) menu.Show(Cursor.Position);
                    }
                };
                once.Start();
            }
        }

        // ------------------------------------------------------------ menu
        private void BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("status: inactive");
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);

            _costItem = new ToolStripMenuItem("opencode: ca. 0.00 $");
            _costItem.Enabled = false;
            menu.Items.Add(_costItem);

            _projectsItem = new ToolStripMenuItem("OpenCode instances");
            _projectsItem.Enabled = true;
            menu.Items.Add(_projectsItem);

            menu.Items.Add(new ToolStripSeparator());

            _resetCostItem = new ToolStripMenuItem("Reset cost count");
            _resetCostItem.Click += delegate { ResetCosts(); };
            menu.Items.Add(_resetCostItem);

            _costInfoItem = new ToolStripMenuItem("Cost info (model - token)");
            _costInfoItem.Click += delegate { ShowCostInfo(); };
            menu.Items.Add(_costInfoItem);

            menu.Items.Add(new ToolStripSeparator());

            _balloonItem = new ToolStripMenuItem("Notification popup");
            _balloonItem.Checked = _settings.Balloon;
            _balloonItem.Click += delegate { _settings.Balloon = !_settings.Balloon; _settings.Save(); RefreshMenu(); };
            menu.Items.Add(_balloonItem);

            _soundItem = new ToolStripMenuItem("Warning sound");
            _soundItem.Checked = _settings.Sound;
            _soundItem.Click += delegate { _settings.Sound = !_settings.Sound; _settings.Save(); RefreshMenu(); };
            menu.Items.Add(_soundItem);

            _flashItem = new ToolStripMenuItem("Blinking icon when waiting");
            _flashItem.Checked = _settings.Flash;
            _flashItem.Click += delegate { _settings.Flash = !_settings.Flash; _settings.Save(); RefreshMenu(); };
            menu.Items.Add(_flashItem);

            _autostartItem = new ToolStripMenuItem("Start with Windows");
            _autostartItem.Checked = _settings.StartWithWindows;
            _autostartItem.Click += OnToggleAutostart;
            menu.Items.Add(_autostartItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem cfg = new ToolStripMenuItem("Open opencode config");
            cfg.Click += delegate { OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode")); };
            menu.Items.Add(cfg);

            ToolStripMenuItem inst = new ToolStripMenuItem("Open installation folder");
            inst.Click += delegate { OpenFolder(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')); };
            menu.Items.Add(inst);

            ToolStripMenuItem log = new ToolStripMenuItem("Open tray config");
            log.Click += delegate { OpenFolder(Settings.ConfigDir); };
            menu.Items.Add(log);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            menu.Opening += delegate { RefreshMenu(); };
            _notify.ContextMenuStrip = menu;
        }

        private void RefreshMenu()
        {
            _balloonItem.Checked = _settings.Balloon;
            _soundItem.Checked = _settings.Sound;
            _flashItem.Checked = _settings.Flash;
            _autostartItem.Checked = _settings.StartWithWindows;

            int live = 0;
            double totalCost = 0.0;
            List<Instance> list;
            lock (_instances)
            {
                list = new List<Instance>(_instances.Values);
                foreach (Instance i in list)
                {
                    if (i.Live) live++;
                    totalCost += i.DisplayCost();
                }
            }

            _statusItem.Text = "stato: " + StateLabel(_state) + " - " + live + " istanze opencode";
            _costItem.Text = "opencode: ca. " + Money(totalCost);

            // elenco delle istanze (progetti): quale sta lavorando, quale e' fermo
            _projectsItem.DropDownItems.Clear();
            if (list.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("no active opencode instance");
                none.Enabled = false;
                _projectsItem.DropDownItems.Add(none);
            }
            else
            {
                list.Sort(delegate (Instance a, Instance b) { return b.LastSeen.CompareTo(a.LastSeen); });
                foreach (Instance i in list)
                {
                    if (!i.Live && i.DisplayCost() <= 0) continue;
                    ToolStripMenuItem it = new ToolStripMenuItem(InstanceLabel(i));
                    it.Enabled = false;
                    _projectsItem.DropDownItems.Add(it);
                }
                if (_projectsItem.DropDownItems.Count == 0)
                {
                    ToolStripMenuItem none = new ToolStripMenuItem("no active opencode instance");
                    none.Enabled = false;
                    _projectsItem.DropDownItems.Add(none);
                }
            }
            _projectsItem.Text = "Istanze opencode (" + live + ")";
        }

        private string InstanceLabel(Instance i)
        {
            string name = string.IsNullOrEmpty(i.Name) ? "?" : i.Name;
            string state = StateLabelShort(i.State);
            string detail = "";
            if (!string.IsNullOrEmpty(i.Activity)) detail = " (" + i.Activity + ")";
            else if (!string.IsNullOrEmpty(i.Reason)) detail = " (" + i.Reason + ")";
            int n = 0;
            foreach (SessionStat s in i.Sessions.Values) if (s.Live) n++;
            string extra = n > 1 ? " - " + n + " sessioni" : "";
            return name + " - " + state + detail + extra + " - " + Money(i.DisplayCost());
        }

        private void ResetCosts()
        {
            lock (_instances)
            {
                foreach (Instance inst in _instances.Values)
                {
                    Dictionary<string, ModelStat> b = new Dictionary<string, ModelStat>();
                    foreach (KeyValuePair<string, ModelStat> kv in inst.Totals)
                        b[kv.Key] = kv.Value.Clone();
                    inst.Base = b;
                }
            }
            SaveBases();
            Log("conteggio costi azzerato");
            RefreshMenu();
        }

        // ------------------------------------------------- basi di conteggio su disco
        private void LoadBases()
        {
            try
            {
                if (!File.Exists(BaseFile)) return;
                foreach (string line in File.ReadAllLines(BaseFile, Encoding.UTF8))
                {
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] f = line.Split('|');
                    if (f.Length != 9) continue;
                    long n;
                    double d;
                    ModelStat m = new ModelStat();
                    m.Provider = f[1];
                    m.Model = f[2];
                    d = double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
                    m.Cost = d;
                    long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out n); m.Input = n;
                    long.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out n); m.Output = n;
                    long.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out n); m.Reasoning = n;
                    long.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out n); m.CacheRead = n;
                    long.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out n); m.CacheWrite = n;
                    Dictionary<string, ModelStat> d2;
                    if (!_baseStore.TryGetValue(f[0], out d2))
                    {
                        d2 = new Dictionary<string, ModelStat>();
                        _baseStore[f[0]] = d2;
                    }
                    d2[m.Key] = m;
                }
            }
            catch { }
        }

        private void SaveBases()
        {
            try
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("# opencode-tray - basi di conteggio (path|provider|model|costo|input|output|reasoning|cacheR|cacheW)\n");
                lock (_instances)
                {
                    foreach (KeyValuePair<string, Instance> kv in _instances)
                    {
                        foreach (KeyValuePair<string, ModelStat> b in kv.Value.Base)
                        {
                            ModelStat m = b.Value;
                            sb.Append(kv.Key).Append('|')
                              .Append(m.Provider).Append('|')
                              .Append(m.Model).Append('|')
                              .Append(m.Cost.ToString("0.000000", CultureInfo.InvariantCulture)).Append('|')
                              .Append(m.Input).Append('|')
                              .Append(m.Output).Append('|')
                              .Append(m.Reasoning).Append('|')
                              .Append(m.CacheRead).Append('|')
                              .Append(m.CacheWrite).Append('\n');
                        }
                    }
                }
                File.WriteAllText(BaseFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ------------------------------------------------------------ finestra stima costi
        private void ShowCostInfo()
        {
            string report = BuildCostReport();
            try
            {
                using (Form f = new Form())
                {
                    f.Text = "opencode - stima costi";
                    f.StartPosition = FormStartPosition.CenterScreen;
                    f.ClientSize = new Size(820, 600);
                    f.MinimumSize = new Size(560, 320);
                    f.Font = new Font("Segoe UI", 9f);

                    FlowLayoutPanel buttons = new FlowLayoutPanel();
                    buttons.Dock = DockStyle.Bottom;
                    buttons.Height = 44;
                    buttons.FlowDirection = FlowDirection.RightToLeft;
                    buttons.Padding = new Padding(8, 8, 8, 4);

                    Button close = new Button();
                    close.Text = "Chiudi";
                    close.AutoSize = true;
                    close.Click += delegate { f.Close(); };
                    Button copy = new Button();
                    copy.Text = "Copia negli appunti";
                    copy.AutoSize = true;
                    copy.Click += delegate
                    {
                        try { Clipboard.SetText(report); } catch { }
                    };
                    buttons.Controls.Add(close);
                    buttons.Controls.Add(copy);

                    TextBox box = new TextBox();
                    box.Multiline = true;
                    box.ReadOnly = true;
                    box.ScrollBars = ScrollBars.Both;
                    box.WordWrap = false;
                    box.Dock = DockStyle.Fill;
                    box.BackColor = Color.White;
                    box.Font = new Font("Consolas", 9.5f);
                    box.Text = report;
                    box.Select(0, 0);
                    box.SelectionLength = 0;

                    f.Controls.Add(box);
                    f.Controls.Add(buttons);
                    f.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossibile aprire la stima costi: " + ex.Message,
                    "opencode - stima costi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string BuildCostReport()
        {
            StringBuilder sb = new StringBuilder();
            List<Instance> list;
            lock (_instances) { list = new List<Instance>(_instances.Values); }
            list.Sort(delegate (Instance a, Instance b) { return b.DisplayCost().CompareTo(a.DisplayCost()); });

            Line(sb, "STIMA COSTI OPENCODE");
            Line(sb, "quanto pagherei se il modello non fosse gratis");
            Line(sb, "prezzi di listino opencode; i modelli \"free\" sono valutati col");
            Line(sb, "gemello a pagamento. Token openrouter esclusi.");
            Line(sb, "");

            double total = 0.0, total24 = 0.0, total7 = 0.0;
            long firstAt = 0;
            bool any = false;

            foreach (Instance inst in list)
            {
                if (!inst.Live && inst.DisplayCost() <= 0) continue;
                any = true;
                double instCost = inst.DisplayCost();
                total += instCost;

                int live = 0;
                foreach (SessionStat s in inst.Sessions.Values)
                {
                    if (!s.Live) continue;
                    live++;
                    total24 += s.Cost24h;
                    total7 += s.Cost7d;
                    if (s.FirstAt > 0 && (firstAt == 0 || s.FirstAt < firstAt)) firstAt = s.FirstAt;
                }

                string name = string.IsNullOrEmpty(inst.Name) ? "?" : inst.Name;
                string head = "== " + name + "  [" + StateLabelShort(inst.State) + "]";
                if (!string.IsNullOrEmpty(inst.Activity)) head += ": " + inst.Activity;
                else if (!string.IsNullOrEmpty(inst.Reason)) head += ": " + inst.Reason;
                if (live > 1) head += " - " + live + " sessioni";
                else if (live == 0) head += " - chiusa";
                head += " " + new string('-', Math.Max(1, REPORT_WIDTH - head.Length));
                Line(sb, head);

                foreach (KeyValuePair<string, ModelStat> kv in inst.Totals)
                {
                    ModelStat m = kv.Value;
                    double c = instCostFor(inst, kv.Key);
                    // il modello resta visibile anche se azzerato (mostra 0)
                    if (c <= 0 && m.Input == 0 && m.Output == 0 && m.Reasoning == 0 &&
                        m.CacheRead == 0 && m.CacheWrite == 0) continue;
                    string label = m.Provider + "/" + m.Model;
                    if (!string.IsNullOrEmpty(m.PricedAs) && m.PricedAs != m.Model)
                        label += "  (prezzo di " + m.PricedAs + ")";
                    Line(sb, "   " + label);
                    Field(sb, "input", inst.DisplayLong(kv.Key, "input"));
                    Field(sb, "output", inst.DisplayLong(kv.Key, "output"));
                    Field(sb, "reasoning", inst.DisplayLong(kv.Key, "reasoning"));
                    Field(sb, "cache letta", inst.DisplayLong(kv.Key, "cacheRead"));
                    Field(sb, "cache scritta", inst.DisplayLong(kv.Key, "cacheWrite"));
                    Line(sb, "   " + Pad("subtotale", 22) + Pad(Money(c), 14, true));
                }
                Line(sb, "   " + Pad("totale progetto", 22) + Pad(Money(instCost), 14, true));
                Line(sb, "");
            }

            if (!any)
                Line(sb, "Nessun dato: riavvia opencode per caricare il plugin aggiornato.");

            Line(sb, new string('=', REPORT_WIDTH));
            Line(sb, Pad("TOTALE STIMATO", 32) + Pad(Money(total), 14, true));
            Line(sb, Pad("ultimi 24 ore", 32) + Pad(Money(total24), 14, true));
            Line(sb, Pad("ultimi 7 giorni", 32) + Pad(Money(total7), 14, true));

            double days = 0.0;
            if (firstAt > 0)
            {
                days = (DateTime.Now - FromEpoch(firstAt)).TotalDays;
                if (days < 0) days = 0;
            }
            // la proiezione ha senso solo con un po' di storia: sotto mezza
            // giornata i dati sono troppo pochi e la stima would be nonsense
            bool enough = days >= 0.5;
            double daily = enough ? total / days : 0.0;
            double week = total7 > 0 ? total7 : daily * 7.0;
            double month = daily * 30.0;
            if (enough)
            {
                Line(sb, Pad("media giornaliera (" + days.ToString("0.0", CultureInfo.InvariantCulture) + " gg)", 32) + Pad(Money(daily), 14, true));
                Line(sb, Pad("proiezione settimanale", 32) + Pad(Money(week), 14, true));
                Line(sb, Pad("proiezione mensile (30 gg)", 32) + Pad(Money(month), 14, true));
            }
            else
            {
                Line(sb, Pad("media giornaliera", 32) + "n/d (serve almeno mezza giornata di dati)");
                Line(sb, Pad("proiezione settimanale", 32) + Pad(Money(week), 14, true));
                Line(sb, Pad("proiezione mensile (30 gg)", 32) + "n/d");
            }
            Line(sb, new string('=', REPORT_WIDTH));
            return sb.ToString();
        }

        private double instCostFor(Instance inst, string key)
        {
            ModelStat m = inst.TotalOf(key);
            if (m == null) return 0.0;
            ModelStat b;
            double baseCost = inst.Base.TryGetValue(key, out b) ? b.Cost : 0.0;
            double v = m.Cost - baseCost;
            return v > 0 ? v : 0.0;
        }

        private static void Line(StringBuilder sb, string text)
        {
            // "\r\n": il TextBox non riconosce il solo "\n" come a capo
            sb.Append(text).Append("\r\n");
        }

        private static void Field(StringBuilder sb, string name, long value)
        {
            sb.Append("      ").Append(Pad(name, 18)).Append(Tok(value)).Append(" tok\r\n");
        }

        private static string Pad(string text, int width)
        {
            return Pad(text, width, false);
        }

        private static string Pad(string text, int width, bool right)
        {
            if (text == null) text = string.Empty;
            if (text.Length > width) text = text.Substring(0, Math.Max(1, width - 1)) + "~";
            if (right) return text.PadLeft(width);
            return text.PadRight(width);
        }

        private static string Tok(long v)
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string Money(double v)
        {
            double a = Math.Abs(v);
            if (a < 1e-9) return "0.00 $";
            if (a < 0.01) return v.ToString("0.0000", CultureInfo.InvariantCulture) + " $";
            if (a < 1.0) return v.ToString("0.000", CultureInfo.InvariantCulture) + " $";
            if (a < 100.0) return v.ToString("0.00", CultureInfo.InvariantCulture) + " $";
            return v.ToString("0", CultureInfo.InvariantCulture) + " $";
        }

        private static string StateLabel(TrayState s)
        {
            if (s == TrayState.Working) return "verde, sta lavorando";
            if (s == TrayState.Attention) return "gialla, in attesa";
            return "nera, inattivo";
        }

        private static string StateLabelShort(TrayState s)
        {
            if (s == TrayState.Working) return "sta lavorando";
            if (s == TrayState.Attention) return "in attesa";
            return "inattivo";
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        // ------------------------------------------------------------ parsing
        // Riga "provider|model|pricedAs|input|output|reasoning|cacheRead|cacheWrite|cost"
        private static Dictionary<string, ModelStat> ParseModels(string line)
        {
            Dictionary<string, ModelStat> d = new Dictionary<string, ModelStat>();
            if (string.IsNullOrEmpty(line)) return d;
            foreach (string rec in line.Split(','))
            {
                if (rec.Length == 0) continue;
                string[] f = rec.Split('|');
                if (f.Length != 9) continue;
                try
                {
                    ModelStat m = new ModelStat();
                    m.Provider = f[0];
                    m.Model = f[1];
                    m.PricedAs = f[2];
                    m.Input = long.Parse(f[3], CultureInfo.InvariantCulture);
                    m.Output = long.Parse(f[4], CultureInfo.InvariantCulture);
                    m.Reasoning = long.Parse(f[5], CultureInfo.InvariantCulture);
                    m.CacheRead = long.Parse(f[6], CultureInfo.InvariantCulture);
                    m.CacheWrite = long.Parse(f[7], CultureInfo.InvariantCulture);
                    m.Cost = double.Parse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture);
                    d[m.Key] = m;
                }
                catch { }
            }
            return d;
        }

        // Riga sessione:
        // sid|project|cwd|state|reason|activity|costo24h|costo7d|primoStep|modelli
        private static List<SessionUpdate> ParseSessions(string blob)
        {
            List<SessionUpdate> list = new List<SessionUpdate>();
            if (string.IsNullOrEmpty(blob)) return list;
            foreach (string line in blob.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                string[] f = line.Split(new char[] { '|' }, 10);
                if (f.Length < 9) continue;
                try
                {
                    SessionUpdate u = new SessionUpdate();
                    u.Sid = f[0];
                    u.Project = f[1];
                    u.Path = f[2];
                    u.State = f[3];
                    u.Reason = f[4];
                    u.Activity = f[5];
                    double d24, d7;
                    double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out d24);
                    double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out d7);
                    u.Cost24h = d24;
                    u.Cost7d = d7;
                    long first;
                    long.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out first);
                    u.FirstAt = first;
                    u.Models = ParseModels(f.Length > 9 ? f[9] : string.Empty);
                    list.Add(u);
                }
                catch { }
            }
            return list;
        }

        private static TrayState ParseState(string s)
        {
            if (s == "working") return TrayState.Working;
            if (s == "attention") return TrayState.Attention;
            return TrayState.Idle;
        }

        private static string InstanceKey(string path, string project)
        {
            string k = string.IsNullOrEmpty(path) ? project : path;
            if (string.IsNullOrEmpty(k)) k = "?";
            return k.ToLowerInvariant();
        }

        // Nome breve di un percorso (ultima cartella)
        private static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string[] parts = path.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[parts.Length - 1] : path;
        }

        // Epoch (ms) -> data locale
        private static DateTime FromEpoch(long ms)
        {
            if (ms <= 0) return DateTime.MinValue;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
        }

        // ------------------------------------------------------------ stato
        private void OnStateReceived(StateUpdate u)
        {
            List<SessionUpdate> updates = ParseSessions(u.Sessions);
            long now = Environment.TickCount;

            lock (_instances)
            {
                foreach (SessionUpdate su in updates)
                {
                    string path = su.Path;
                    if (string.IsNullOrEmpty(path)) path = u.Cwd;
                    string project = string.IsNullOrEmpty(su.Project) ? u.Project : su.Project;
                    if (string.IsNullOrEmpty(project)) project = BaseName(path);
                    if (string.IsNullOrEmpty(project)) project = "?";

                    string key = InstanceKey(path, project);
                    Instance inst;
                    if (!_instances.TryGetValue(key, out inst))
                    {
                        inst = new Instance();
                        inst.Path = path;
                        inst.Name = project;
                        _instances[key] = inst;
                        // counting baseline already saved previously
                        Dictionary<string, ModelStat> saved;
                        if (_baseStore.TryGetValue(key, out saved))
                        {
                            foreach (KeyValuePair<string, ModelStat> b in saved)
                                inst.Base[b.Key] = b.Value.Clone();
                        }
                        Log("istanza opencode: " + (string.IsNullOrEmpty(project) ? "?" : project) + " (" + path + ")");
                    }
                    if (!string.IsNullOrEmpty(project)) inst.Name = project;
                    if (!string.IsNullOrEmpty(path)) inst.Path = path;

                    SessionStat sess;
                    if (!inst.Sessions.TryGetValue(su.Sid, out sess))
                    {
                        sess = new SessionStat();
                        sess.Sid = su.Sid;
                        inst.Sessions[su.Sid] = sess;
                    }
                    sess.Project = project;
                    sess.Path = path;
                    sess.State = ParseState(su.State);
                    sess.Reason = su.Reason ?? string.Empty;
                    sess.Activity = su.Activity ?? string.Empty;
                    sess.LastSeen = now;
                    sess.Live = true;
                    sess.Cost24h = su.Cost24h;
                    sess.Cost7d = su.Cost7d;
                    if (su.FirstAt > 0) sess.FirstAt = su.FirstAt;

                    foreach (KeyValuePair<string, ModelStat> kv in su.Models)
                    {
                        ModelStat last;
                        if (!sess.Last.TryGetValue(kv.Key, out last)) last = null;
                        ModelStat delta = ModelStat.Delta(kv.Value, last);
                        sess.Last[kv.Key] = kv.Value.Clone();
                        ModelStat tot;
                        if (!sess.Totals.TryGetValue(kv.Key, out tot))
                        {
                            tot = new ModelStat();
                            tot.Provider = kv.Value.Provider;
                            tot.Model = kv.Value.Model;
                            tot.PricedAs = kv.Value.PricedAs;
                            sess.Totals[kv.Key] = tot;
                        }
                        tot.Add(delta);
                        inst.Accumulate(kv.Key, delta);
                    }

                    inst.LastSeen = now;
                }
            }

            Recompute(true);
        }

        private void Recompute(bool allowNotify)
        {
            if (_testUntil != 0 && Environment.TickCount - _testUntil < 0) return;
            TrayState next = TrayState.Idle;
            string reason = string.Empty;
            long bestSeen = long.MinValue;

            lock (_instances)
            {
                long now = Environment.TickCount;
                long timeout = _settings.HeartbeatTimeoutSec * 1000L;

                foreach (KeyValuePair<string, Instance> kv in _instances)
                {
                    Instance inst = kv.Value;
                    inst.Live = false;
                    inst.State = TrayState.Idle;
                    inst.Reason = string.Empty;
                    inst.Activity = string.Empty;

                    foreach (KeyValuePair<string, SessionStat> sk in inst.Sessions)
                    {
                        SessionStat sess = sk.Value;
                        bool live = (now - sess.LastSeen) <= timeout;
                        sess.Live = live;
                        if (!live) continue;
                        inst.Live = true;
                        if (inst.State == TrayState.Idle || sess.State > inst.State)
                        {
                            inst.State = sess.State;
                            inst.Reason = sess.Reason;
                            inst.Activity = sess.Activity;
                        }
                        else if (sess.State == inst.State && string.IsNullOrEmpty(inst.Activity))
                        {
                            inst.Activity = sess.Activity;
                        }
                    }
                }

                foreach (KeyValuePair<string, Instance> kv in _instances)
                {
                    Instance inst = kv.Value;
                    if (!inst.Live) continue;
                    if (inst.State > next || (inst.State == next && inst.LastSeen > bestSeen))
                    {
                        if (inst.State > next || string.IsNullOrEmpty(reason))
                        {
                            reason = string.IsNullOrEmpty(inst.Reason) ? inst.Activity : inst.Reason;
                        }
                        next = inst.State;
                        bestSeen = inst.LastSeen;
                    }
                }
            }

            Apply(next, reason, allowNotify);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_flashLeft > 0)
            {
                _flashLeft--;
                _flashOn = !_flashOn;
                if (_flashLeft == 0) _flashOn = true;
                UpdateIcon();
            }
            Recompute(true);
            // RefreshMenu tocca i controlli del menu: gira solo sul thread UI
            if (_uiDirty)
            {
                _uiDirty = false;
                RefreshMenu();
            }
        }

        private void ApplyTest(TrayState s)
        {
            _testUntil = Environment.TickCount + 15000;
            Apply(s, s == TrayState.Attention ? "prova di notifica" : string.Empty, true);
        }

        private void Apply(TrayState s, string reason, bool allowNotify)
        {
            reason = reason ?? string.Empty;
            bool changed = s != _state;
            bool reasonChanged = reason != _reason;
            _state = s;
            _reason = reason;

            if (!changed && !reasonChanged) return;

            if (changed && s != TrayState.Attention) _flashLeft = 0;
            UpdateIcon();
            // il menu viene aggiornato dal timer (thread UI): il thread che
            // riceve gli stati non deve toccare i controlli WinForms
            _uiDirty = true;
            if (changed || reasonChanged)
                Log("stato -> " + s + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
            if (changed && s == TrayState.Attention && allowNotify) Alert(reason);
        }

        private void UpdateIcon()
        {
            Icon ico;
            if (_flashLeft > 0)
                ico = _flashOn ? _iconAttention : _iconDim;
            else if (_state == TrayState.Attention)
                ico = _iconAttention;
            else if (_state == TrayState.Working)
                ico = _iconWorking;
            else
                ico = _iconIdle;

            try
            {
                _notify.Icon = ico;
                _notify.Text = BuildTooltip();
            }
            catch { }
        }

        private string BuildTooltip()
        {
            string head;
            if (_state == TrayState.Attention) head = "ATTENZIONE";
            else if (_state == TrayState.Working) head = "sta lavorando";
            else head = "inattivo";
            string text = "opencode: " + head;
            if (_state != TrayState.Idle && _reason.Length > 0) text += " - " + _reason;
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            return text;
        }

        private void Alert(string reason)
        {
            string text = string.IsNullOrEmpty(reason)
                ? "opencode ha finito e sta aspettando una tua risposta."
                : reason + ".";
            if (_settings.Balloon)
            {
                try { _notify.ShowBalloonTip(10000, "opencode - attenzione", text, ToolTipIcon.Warning); }
                catch { }
            }
            if (_settings.Flash) { _flashLeft = 14; _flashOn = true; }
            if (_settings.Sound)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
        }

        private void OnDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ContextMenuStrip menu = _notify.ContextMenuStrip;
                if (menu != null) menu.Show(Cursor.Position);
            }
        }

        private void HookExplorer()
        {
            try
            {
                Form helper = new Form();
                helper.ShowInTaskbar = false;
                helper.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                helper.StartPosition = FormStartPosition.Manual;
                helper.Location = new Point(-32000, -32000);
                helper.Size = new Size(1, 1);
                helper.Show();
                helper.Hide();
                _messageWindow = new TrayMessageWindow(helper.Handle);
                _messageWindow.TaskbarCreated += delegate
                {
                    try
                    {
                        _notify.Visible = false;
                        _notify.Visible = true;
                        UpdateIcon();
                    }
                    catch { }
                };
            }
            catch { }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(path);
            }
            catch { }
        }

        private void WriteIconFile()
        {
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "OpencodeTray.ico");
                if (!File.Exists(file)) File.WriteAllBytes(file, LogoFactory.BuildIco(LogoFactory.WorkingColor));
            }
            catch { }
        }

        // ------------------------------------------------------------ avvio automatico
        private static string StartupFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Startup");
            }
        }

        private static string StartupLinkPath
        {
            get { return Path.Combine(StartupFolder, StartupLinkName); }
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            try
            {
                if (_settings.StartWithWindows)
                {
                    Directory.CreateDirectory(StartupFolder);
                    WshShortcut.Create(StartupLinkPath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "OpencodeTray.exe"));
                }
                else if (File.Exists(StartupLinkPath))
                {
                    File.Delete(StartupLinkPath);
                }
            }
            catch { }
            _autostartItem.Checked = _settings.StartWithWindows;
            _settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); } } catch { }
                try { if (_notify != null) { _notify.Visible = false; _notify.Dispose(); } } catch { }
                try { if (_server != null) _server.Dispose(); } catch { }
                try { if (_iconIdle != null) _iconIdle.Dispose(); } catch { }
                try { if (_iconWorking != null) _iconWorking.Dispose(); } catch { }
                try { if (_iconAttention != null) _iconAttention.Dispose(); } catch { }
                try { if (_iconDim != null) _iconDim.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayMessageWindow : NativeWindow
    {
        public event Action TaskbarCreated;
        private readonly int _msg;

        public TrayMessageWindow(IntPtr handle)
        {
            _msg = RegisterWindowMessage("TaskbarCreated");
            AssignHandle(handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (_msg != 0 && m.Msg == _msg)
            {
                Action handler = TaskbarCreated;
                if (handler != null) { handler(); return; }
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpName);
    }

    internal static class WshShortcut
    {
        public static void Create(string linkPath, string target)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            try
            {
                object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, link, new object[] { 7 });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }

    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            try { _mutex = new Mutex(true, "Local\\OpencodeTray.SingleInstance", out created); }
            catch { created = true; }
            if (!created) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(args));
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }
}
