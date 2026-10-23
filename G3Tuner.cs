// G3 Tuner - headphone correction, microphone tuning and noise suppression for Creative Sound Blaster G3
// Works on top of Equalizer APO (EQ + RNNoise VST) and optionally NVIDIA Broadcast (GPU noise removal).
// Build: powershell -ExecutionPolicy Bypass -File build.ps1
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace G3Tuner
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Contains("--install-rnnoise")));
        }
    }

    // ---------------------------------------------------------------- settings

    class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "G3Tuner");
        public static string ProfilePath { get { return Path.Combine(Dir, "headphone_profile.txt"); } }
        public static string IndexCachePath { get { return Path.Combine(Dir, "autoeq_index.md"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) s.values[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, values.Select(kv => kv.Key + "=" + kv.Value).ToArray(), Encoding.UTF8);
        }

        public string Get(string key, string def)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : def;
        }

        public int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public bool GetBool(string key, bool def) { return Get(key, def ? "1" : "0") == "1"; }

        public void Set(string key, string val) { values[key] = val; }
        public void Set(string key, int val) { values[key] = val.ToString(CultureInfo.InvariantCulture); }
        public void Set(string key, bool val) { values[key] = val ? "1" : "0"; }
    }

    // ---------------------------------------------------------------- Equalizer APO

    static class EqApo
    {
        public static string ConfigDir()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                {
                    if (k != null)
                    {
                        var p = k.GetValue("ConfigPath") as string;
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                    }
                }
            }
            catch { }
            const string def = @"C:\Program Files\EqualizerAPO\config";
            return Directory.Exists(def) ? def : null;
        }

        public static string InstallDir()
        {
            var c = ConfigDir();
            return c == null ? null : Path.GetDirectoryName(c);
        }
    }

    // Shared-mode format Windows uses for the G3 microphone (read from the audio endpoint registry).
    static class MicFormat
    {
        public static bool TryGet(out int sampleRate, out int channels)
        {
            sampleRate = 0; channels = 0;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var cap = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture"))
                {
                    if (cap == null) return false;
                    foreach (var id in cap.GetSubKeyNames())
                    {
                        using (var dev = cap.OpenSubKey(id))
                        using (var props = cap.OpenSubKey(id + @"\Properties"))
                        {
                            if (dev == null || props == null) continue;
                            if (Convert.ToInt32(dev.GetValue("DeviceState", 0)) != 1) continue;
                            var name = props.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                            var iface = props.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                            if (name != "Microphone" || iface == null || !iface.Contains("Sound Blaster G3")) continue;
                            // PROPVARIANT blob: 8-byte header, then WAVEFORMATEX(TENSIBLE)
                            var blob = props.GetValue("{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0") as byte[];
                            if (blob == null || blob.Length < 24) return false;
                            channels = BitConverter.ToUInt16(blob, 10);
                            sampleRate = (int)BitConverter.ToUInt32(blob, 12);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // ---------------------------------------------------------------- RNNoise (werman/noise-suppression-for-voice VST2)

    static class RnNoise
    {
        public const string ZipUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";

        public static string DefaultDir()
        {
            var inst = EqApo.InstallDir();
            return inst == null ? null : Path.Combine(inst, "VSTPlugins", "G3Tuner-RNNoise");
        }

        // Prefers the build matching the mic's channel count; the stereo build gates both channels together.
        public static string FindDll(string dir, int channels)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            var preferred = Path.Combine(dir, channels == 1 ? "rnnoise_mono.dll" : "rnnoise_stereo.dll");
            var other = Path.Combine(dir, channels == 1 ? "rnnoise_stereo.dll" : "rnnoise_mono.dll");
            if (File.Exists(preferred)) return preferred;
            return File.Exists(other) ? other : null;
        }

        // Plugin state in JUCE's binary XML format ("VC2!" + uint32 length + UTF-8 XML + NUL), base64 for Equalizer APO.
        public static string ChunkData(int vadPercent, int graceMs)
        {
            var inv = CultureInfo.InvariantCulture;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?> <RNNoise>" +
                "<PARAM id=\"vad_grace_period\" value=\"" + (graceMs / 10).ToString(inv) + ".0\"/>" +
                "<PARAM id=\"vad_retroactive_grace_period\" value=\"0.0\"/>" +
                "<PARAM id=\"vad_threshold\" value=\"" + (vadPercent / 100.0).ToString("0.00", inv) + "\"/>" +
                "</RNNoise>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x21324356); // "VC2!"
                w.Write(bytes.Length);
                w.Write(bytes);
                w.Write((byte)0);
                w.Flush();
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // Copies the 64-bit VST2 builds (rnnoise_mono.dll / rnnoise_stereo.dll) out of the release zip.
        public static void ExtractVst2(string zipPath, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            int found = 0;
            using (var zip = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    var name = e.Name.ToLowerInvariant();
                    if (name != "rnnoise_mono.dll" && name != "rnnoise_stereo.dll") continue;
                    if (e.FullName.IndexOf(".vst3", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    byte[] data;
                    using (var src = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        src.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    if (!IsX64(data)) continue;
                    File.WriteAllBytes(Path.Combine(targetDir, name), data);
                    found++;
                }
            }
            if (found == 0) throw new Exception("ไม่พบไฟล์ปลั๊กอิน VST 64-bit ใน zip");
        }

        static bool IsX64(byte[] pe)
        {
            if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return false;
            int off = BitConverter.ToInt32(pe, 0x3C);
            return off > 0 && off + 6 <= pe.Length && BitConverter.ToUInt16(pe, off + 4) == 0x8664;
        }
    }

    static class ConfigBuilder
    {
        static string F(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }

        // Keep the generated file ASCII-only; Equalizer APO does not need anything else.
        public static string Build(Settings s, string headphoneProfile, bool forceNoiseOff)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Generated by G3 Tuner - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine("# Changes made here are overwritten. Use the G3 Tuner app instead.");
            sb.AppendLine();

            // Headphone correction (G3 has both "Headset" and "Speakers" outputs depending on mode)
            sb.AppendLine("# ---------- Headphone: " + Ascii(s.Get("hp_name", "(none)")) + " ----------");
            sb.AppendLine("Device: Sound Blaster G3 Headset; Sound Blaster G3 Speakers");
            if (s.GetBool("hp_enabled", true) && !string.IsNullOrEmpty(headphoneProfile))
            {
                int bass = s.GetInt("hp_bass", 0), treble = s.GetInt("hp_treble", 0);
                double preamp = 0;
                var filters = new List<string>();
                foreach (var raw in headphoneProfile.Split('\n'))
                {
                    var line = raw.Trim();
                    var m = Regex.Match(line, @"^Preamp:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase);
                    if (m.Success) preamp = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    else if (line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase)) filters.Add(line);
                }
                // Extra headroom for user boosts so nothing clips
                preamp -= Math.Max(0, Math.Max(bass, treble));
                sb.AppendLine("Preamp: " + F(preamp) + " dB");
                foreach (var f in filters) sb.AppendLine(f);
                if (bass != 0) sb.AppendLine("Filter: ON LSC Fc 105 Hz Gain " + F(bass) + " dB Q 0.70");
                if (treble != 0) sb.AppendLine("Filter: ON HSC Fc 8000 Hz Gain " + F(treble) + " dB Q 0.70");
            }
            else
            {
                sb.AppendLine("# headphone EQ disabled");
            }
            sb.AppendLine();

            // Microphone chain: high-pass -> RNNoise -> tone EQ -> gain.
            // (With NVIDIA Broadcast mode, the GPU noise removal happens after this chain.)
            sb.AppendLine("# ---------- Microphone ----------");
            sb.AppendLine("Device: Sound Blaster G3 Microphone");
            bool eq = s.GetBool("mic_enabled", true);
            string rnDll = null;
            if (!forceNoiseOff && s.GetInt("nr_mode", 0) == 0)
            {
                int rate, ch;
                if (!MicFormat.TryGet(out rate, out ch)) ch = 2;
                rnDll = RnNoise.FindDll(s.Get("rn_dir", RnNoise.DefaultDir()), ch);
            }
            if (eq) sb.AppendLine("Filter: ON HP Fc " + s.GetInt("mic_hp", 90) + " Hz");
            if (rnDll != null)
                sb.AppendLine("VSTPlugin: Library \"" + rnDll + "\" ChunkData \"" +
                              RnNoise.ChunkData(s.GetInt("nr_vad", 60), s.GetInt("nr_grace", 200)) + "\"");
            else
                sb.AppendLine("# noise suppression off");
            if (eq)
            {
                int mud = s.GetInt("mic_mud", -2);
                int presence = s.GetInt("mic_presence", 3);
                int lp = s.GetInt("mic_lp", 15);
                int gain = s.GetInt("mic_gain", 0);
                if (mud != 0) sb.AppendLine("Filter: ON PK Fc 250 Hz Gain " + F(mud) + " dB Q 1.20");
                if (presence != 0) sb.AppendLine("Filter: ON PK Fc 4000 Hz Gain " + F(presence) + " dB Q 0.90");
                if (lp < 20) sb.AppendLine("Filter: ON LPQ Fc " + (lp * 1000) + " Hz Q 0.71");
                if (gain != 0) sb.AppendLine("Preamp: " + F(gain) + " dB");
            }
            else
            {
                sb.AppendLine("# mic EQ disabled");
            }
            sb.AppendLine();
            sb.AppendLine("Device: all");
            return sb.ToString();
        }

        static string Ascii(string s) { return Regex.Replace(s, @"[^\x20-\x7E]", "?"); }
    }

    // ---------------------------------------------------------------- AutoEq database

    class AutoEqEntry
    {
        public string Name, Path, Source;
        public override string ToString() { return Name + "   —   " + Source; }
        public string ProfileUrl
        {
            get
            {
                var rel = Path.Substring(2); // strip "./"
                var folder = rel.Substring(rel.LastIndexOf('/') + 1);
                return "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/" + rel + "/" + folder + "%20ParametricEQ.txt";
            }
        }
    }

    static class AutoEq
    {
        const string IndexUrl = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/INDEX.md";

        public static List<AutoEqEntry> LoadIndex()
        {
            string text = null;
            try
            {
                text = Download(IndexUrl);
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(Settings.IndexCachePath, text, Encoding.UTF8);
            }
            catch
            {
                if (File.Exists(Settings.IndexCachePath)) text = File.ReadAllText(Settings.IndexCachePath, Encoding.UTF8);
                else throw;
            }
            var list = new List<AutoEqEntry>();
            var rx = new Regex(@"^- \[(.*?)\]\((\./.*)\) by (.*)$");
            foreach (var raw in text.Split('\n'))
            {
                var m = rx.Match(raw.TrimEnd('\r'));
                if (m.Success) list.Add(new AutoEqEntry { Name = m.Groups[1].Value, Path = m.Groups[2].Value, Source = m.Groups[3].Value });
            }
            return list;
        }

        public static string Download(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "G3Tuner";
                return wc.DownloadString(url);
            }
        }
    }

    // ---------------------------------------------------------------- audio (winmm)

    static class WinMM
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
            public uint dwSupport;
        }

        public const int WAVE_MAPPED = 0x0004;
        public const uint WHDR_DONE = 0x0001;

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveOutGetDevCaps(IntPtr id, ref WAVEOUTCAPS caps, int size);
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, IntPtr id, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);
    }

    class DevItem
    {
        public int Index; public string Name;
        public override string ToString() { return Name; }
    }

    static class Audio
    {
        public const int Rate = 48000;

        public static List<DevItem> InputDevices()
        {
            var list = new List<DevItem>();
            int n = WinMM.waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WinMM.WAVEINCAPS();
                if (WinMM.waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WinMM.WAVEINCAPS))) == 0)
                    list.Add(new DevItem { Index = i, Name = caps.szPname });
            }
            return list;
        }

        public static List<string> OutputDeviceNames()
        {
            var list = new List<string>();
            int n = WinMM.waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WinMM.WAVEOUTCAPS();
                if (WinMM.waveOutGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WinMM.WAVEOUTCAPS))) == 0)
                    list.Add(caps.szPname);
            }
            return list;
        }

        // Records mono 16-bit 48 kHz from the given device (blocking).
        public static short[] Record(int device, int seconds, Action<double> progress)
        {
            var fmt = new WinMM.WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16,
                nBlockAlign = 2, nAvgBytesPerSec = Rate * 2, cbSize = 0
            };
            IntPtr h;
            int r = WinMM.waveInOpen(out h, new IntPtr(device), ref fmt, IntPtr.Zero, IntPtr.Zero, WinMM.WAVE_MAPPED);
            if (r != 0) throw new Exception("เปิดไมค์ไม่ได้ (waveInOpen error " + r + ")");

            int bytes = Rate * 2 * seconds;
            IntPtr data = Marshal.AllocHGlobal(bytes);
            int hs = Marshal.SizeOf(typeof(WinMM.WAVEHDR));
            IntPtr ph = Marshal.AllocHGlobal(hs);
            Marshal.StructureToPtr(new WinMM.WAVEHDR { lpData = data, dwBufferLength = (uint)bytes }, ph, false);
            try
            {
                WinMM.waveInPrepareHeader(h, ph, hs);
                WinMM.waveInAddBuffer(h, ph, hs);
                WinMM.waveInStart(h);
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    var cur = (WinMM.WAVEHDR)Marshal.PtrToStructure(ph, typeof(WinMM.WAVEHDR));
                    if ((cur.dwFlags & WinMM.WHDR_DONE) != 0 || sw.Elapsed.TotalSeconds > seconds + 3) break;
                    if (progress != null) progress(Math.Min(1.0, sw.Elapsed.TotalSeconds / seconds));
                    Thread.Sleep(50);
                }
                WinMM.waveInReset(h);
                var done = (WinMM.WAVEHDR)Marshal.PtrToStructure(ph, typeof(WinMM.WAVEHDR));
                int n = (int)done.dwBytesRecorded / 2;
                var samples = new short[n];
                Marshal.Copy(data, samples, 0, n);
                WinMM.waveInUnprepareHeader(h, ph, hs);
                return samples;
            }
            finally
            {
                WinMM.waveInClose(h);
                Marshal.FreeHGlobal(ph);
                Marshal.FreeHGlobal(data);
            }
        }

        public static void WriteWav(string path, short[] samples)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int dataLen = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
                w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
                foreach (var s in samples) w.Write(s);
            }
        }
    }

    class Analysis
    {
        public double SpeechDb, NoiseDb, PeakDb;
        public double Snr { get { return SpeechDb - NoiseDb; } }

        public static Analysis Of(short[] s)
        {
            const int frame = Audio.Rate / 50; // 20 ms
            var levels = new List<double>();
            double peak = 0;
            // skip the first 200 ms (device start-up click)
            for (int start = Audio.Rate / 5; start + frame <= s.Length; start += frame)
            {
                double sum = 0;
                for (int i = start; i < start + frame; i++)
                {
                    double v = s[i] / 32768.0;
                    sum += v * v;
                    if (Math.Abs(v) > peak) peak = Math.Abs(v);
                }
                levels.Add(ToDb(Math.Sqrt(sum / frame)));
            }
            if (levels.Count == 0) return new Analysis { SpeechDb = -100, NoiseDb = -100, PeakDb = -100 };
            levels.Sort();
            return new Analysis
            {
                NoiseDb = levels[(int)(levels.Count * 0.10)],
                SpeechDb = levels[(int)(levels.Count * 0.95)],
                PeakDb = ToDb(peak)
            };
        }

        static double ToDb(double x) { return x < 1e-5 ? -100 : 20 * Math.Log10(x); }

        public string Describe()
        {
            string noise = NoiseDb <= -99 ? "เงียบสนิท" : NoiseDb.ToString("0") + " dBFS";
            var sb = new StringBuilder();
            sb.Append("เสียงพูด " + SpeechDb.ToString("0") + " dBFS   |   เสียงซ่าตอนเงียบ " + noise +
                      "   |   ความชัด (SNR) " + Snr.ToString("0") + " dB   |   Peak " + PeakDb.ToString("0.0") + " dBFS\r\n");
            if (SpeechDb < -50) sb.Append("ไม่ได้ยินเสียงพูด ตรวจว่าเลือกไมค์ถูกตัว และเปิดสิทธิ์ใช้ไมค์ใน Windows แล้ว");
            else if (PeakDb > -1) sb.Append("เสียงแตก (clipping) ให้ลดระดับไมค์ใน Windows หรือลด \"ความดัง\" ในแท็บไมค์");
            else if (SpeechDb < -32) sb.Append("เสียงเบาไป ให้เพิ่มระดับไมค์ใน Windows หรือเพิ่ม \"ความดัง\" ในแท็บไมค์");
            else if (Snr >= 50) sb.Append("ยอดเยี่ยม: เสียงชัด แทบไม่มีเสียงซ่า");
            else if (Snr >= 40) sb.Append("ดี: เสียงชัด มีเสียงพื้นหลังเล็กน้อย");
            else if (Snr >= 28) sb.Append("พอใช้: ยังได้ยินเสียงซ่า ลองเปิด Noise Removal ใน NVIDIA Broadcast หรือลดเกนไมค์");
            else sb.Append("เสียงซ่ามาก: ลดระดับไมค์ใน Windows ลง แล้วใช้ NVIDIA Broadcast ตัดเสียงรบกวน");
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- UI helpers

    class Slider
    {
        public TrackBar Bar;
        public Label Value;
        public Func<int, string> Format;
        public int V { get { return Bar.Value; } set { Bar.Value = Math.Max(Bar.Minimum, Math.Min(Bar.Maximum, value)); Value.Text = Format(Bar.Value); } }
    }

    // ---------------------------------------------------------------- main window

    class MainForm : Form
    {
        readonly Settings settings = Settings.Load();
        readonly System.Windows.Forms.Timer applyTimer = new System.Windows.Forms.Timer { Interval = 400 };
        readonly Font uiFont = new Font("Leelawadee UI", 10f);
        readonly Font boldFont = new Font("Leelawadee UI", 10f, FontStyle.Bold);
        readonly Font titleFont = new Font("Leelawadee UI", 15f, FontStyle.Bold);
        bool loading;
        readonly bool autoInstall;

        // start tab
        Label lblG3Out, lblG3Mic, lblEqApo, lblRnNoise, lblBroadcast;
        // headphone tab
        TextBox txtSearch, txtProfilePreview;
        ListBox lstHeadphones;
        Label lblProfile;
        CheckBox chkHp;
        Slider sBass, sTreble;
        List<AutoEqEntry> index;
        // mic tab
        ComboBox cmbNoise, cmbPreset;
        Label lblRnStatus, lblMicFormat, lblNoiseNote;
        Button btnRnInstall, btnRnBrowse;
        ProgressBar prgInstall;
        CheckBox chkMic;
        Slider sVad, sGrace, sHp, sMud, sPresence, sLp, sGain;
        // test tab
        ComboBox cmbDevA, cmbDevB;
        CheckBox chkCompare;
        Button btnRecord, btnPlayA, btnPlayB;
        ProgressBar prgRecord;
        Label lblTitleA, lblTitleB, lblResA, lblResB;
        SoundPlayer player;
        // footer
        Label lblStatus;

        public MainForm(bool autoInstall)
        {
            this.autoInstall = autoInstall;
            Text = "G3 Tuner — Sound Blaster G3";
            Font = uiFont;
            ClientSize = new Size(820, 760);
            MinimumSize = new Size(760, 640);
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
            tabs.TabPages.Add(BuildStartTab());
            tabs.TabPages.Add(BuildHeadphoneTab());
            tabs.TabPages.Add(BuildMicTab());
            tabs.TabPages.Add(BuildTestTab());

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10) };
            var btnApply = new Button { Text = "บันทึกและใช้งาน", Dock = DockStyle.Right, Width = 170, Font = boldFont };
            btnApply.Click += delegate { Apply(true); };
            lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
            footer.Controls.Add(lblStatus);
            footer.Controls.Add(btnApply);

            Controls.Add(tabs);
            Controls.Add(footer);

            applyTimer.Tick += delegate
            {
                applyTimer.Stop();
                if (!Apply(false)) lblStatus.Text = "ยังไม่ได้ใช้งาน: กด \"บันทึกและใช้งาน\" ครั้งแรกก่อน หลังจากนั้นการปรับจะมีผลทันที";
            };

            LoadSettingsToUi();
            RefreshStatus();
            RefreshDevices();
            LoadIndexAsync();

            // Relaunched as administrator from the "install noise suppression" button: continue the install
            if (autoInstall) Shown += delegate { tabs.SelectedIndex = 2; InstallRnNoise(); };
        }

        // ------------------------------------------------------------ tab: start

        TabPage BuildStartTab()
        {
            var page = new TabPage("เริ่มต้น") { Padding = new Padding(16), AutoScroll = true };
            var y = 14;
            page.Controls.Add(new Label { Text = "Sound Blaster G3 Tuner", Font = titleFont, Location = new Point(16, y), AutoSize = true });
            y += 40;
            page.Controls.Add(new Label { Text = "ปรับเสียงหูฟังให้ตรงตามผลวัดจริง และจูนไมค์ให้ชัดด้วย AI ตัดเสียงรบกวน", Location = new Point(18, y), AutoSize = true, ForeColor = Color.DimGray });
            y += 36;

            lblG3Out = StatusLabel(page, ref y);
            lblG3Mic = StatusLabel(page, ref y);
            lblEqApo = StatusLabel(page, ref y);
            lblRnNoise = StatusLabel(page, ref y);
            lblBroadcast = StatusLabel(page, ref y);

            var btnRefresh = new Button { Text = "ตรวจใหม่", Location = new Point(18, y + 4), Size = new Size(110, 32) };
            btnRefresh.Click += delegate { RefreshStatus(); RefreshDevices(); };
            page.Controls.Add(btnRefresh);
            y += 48;

            var guide = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
                Location = new Point(18, y), Size = new Size(760, 322), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Text =
                    "ขั้นตอนตั้งค่า (ทำครั้งเดียว)\r\n\r\n" +
                    "1) ติดตั้ง Equalizer APO แล้วในหน้าต่าง Device Selector ที่ขึ้นตอนท้ายการติดตั้ง:\r\n" +
                    "     • แท็บ Playback devices ให้ติ๊ก \"Headset\" และ \"Speakers\" ของ Sound Blaster G3\r\n" +
                    "     • แท็บ Capture devices ให้ติ๊ก \"Microphone\" ของ Sound Blaster G3\r\n" +
                    "     • กด OK แล้วรีสตาร์ทเครื่อง\r\n\r\n" +
                    "2) ตัดเสียงรบกวน เลือกอย่างใดอย่างหนึ่ง:\r\n" +
                    "     A. ตัวตัดเสียงรบกวนในตัว G3 Tuner (ง่ายสุด ไม่ต้องลงโปรแกรมเพิ่ม)\r\n" +
                    "         ไปที่แท็บ \"ไมค์\" แล้วกด \"ติดตั้งตัวตัดเสียงรบกวน\"\r\n" +
                    "         จากนั้นในเกมหรือ Discord ให้เลือกไมค์ \"Microphone (Sound Blaster G3)\"\r\n" +
                    "     B. NVIDIA Broadcast (AI บน GPU เหมาะกับห้องที่เสียงดังมาก)\r\n" +
                    "         ตั้ง Source เป็น Microphone (Sound Blaster G3) แล้วเปิด Noise Removal และ Room Echo Removal\r\n" +
                    "         จากนั้นในเกมหรือ Discord ให้เลือกไมค์ \"Microphone (NVIDIA Broadcast)\"\r\n\r\n" +
                    "3) ตั้งระดับไมค์ของ G3 ใน Windows ไว้ราว 70–85 ไม่ต้องดันถึง 100\r\n" +
                    "     เพราะเกนไมค์ที่สูงเกินไปเป็นต้นเหตุหลักของเสียงซ่า\r\n\r\n" +
                    "4) เลือกรุ่นหูฟังในแท็บ \"หูฟัง\" และจูนไมค์ในแท็บ \"ไมค์\" แล้วกด \"บันทึกและใช้งาน\"\r\n\r\n" +
                    "5) ไปที่แท็บ \"ทดสอบไมค์\" เพื่ออัดเสียงเทียบตอนเปิดและปิดการตัดเสียงรบกวน"
            };
            page.Controls.Add(guide);
            y += 334;

            AddLinkButton(page, "ดาวน์โหลด Equalizer APO", 18, y, delegate { OpenUrl("https://sourceforge.net/projects/equalizerapo/"); });
            AddLinkButton(page, "ดาวน์โหลด NVIDIA Broadcast", 208, y, delegate { OpenUrl("https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-app/"); });
            AddLinkButton(page, "Device Selector (EQ APO)", 398, y, delegate
            {
                var dir = EqApo.InstallDir();
                var exe = dir == null ? null : Path.Combine(dir, "DeviceSelector.exe");
                if (exe != null && File.Exists(exe)) Run(exe, null, true);
                else MessageBox.Show(this, "ยังไม่พบ Equalizer APO ในเครื่องครับ", Text);
            });
            AddLinkButton(page, "ตั้งค่าไมค์ใน Windows", 588, y, delegate { Run("control.exe", "mmsys.cpl,,1", false); });
            return page;
        }

        Label StatusLabel(Control parent, ref int y)
        {
            var l = new Label { Location = new Point(18, y), AutoSize = true, Font = boldFont };
            parent.Controls.Add(l);
            y += 28;
            return l;
        }

        void AddLinkButton(Control parent, string text, int x, int y, EventHandler onClick)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(182, 34) };
            b.Click += onClick;
            parent.Controls.Add(b);
        }

        void RefreshStatus()
        {
            var outs = Audio.OutputDeviceNames();
            var ins = Audio.InputDevices().Select(d => d.Name).ToList();
            SetStatus(lblG3Out, outs.Any(n => n.Contains("Sound Blaster G3")), "พบช่องเสียงออกของ Sound Blaster G3", "ไม่พบช่องเสียงออกของ G3 ตรวจว่าเสียบสาย USB แล้ว");
            SetStatus(lblG3Mic, ins.Any(n => n.Contains("Sound Blaster G3") && n.StartsWith("Microphone")), "พบไมค์ของ Sound Blaster G3", "ไม่พบไมค์ของ G3");
            SetStatus(lblEqApo, EqApo.ConfigDir() != null, "ติดตั้ง Equalizer APO แล้ว", "ยังไม่ได้ติดตั้ง Equalizer APO (จำเป็นสำหรับ EQ หูฟัง การจูนไมค์ และตัวตัดเสียงรบกวน)");

            int mode = settings.GetInt("nr_mode", 0);
            bool rn = RnNoiseDll() != null;
            bool broadcast = ins.Any(n => n.Contains("NVIDIA Broadcast"));
            if (mode == 0)
                SetStatus(lblRnNoise, rn, "ติดตั้งตัวตัดเสียงรบกวน (RNNoise) แล้ว", "ยังไม่ได้ติดตั้งตัวตัดเสียงรบกวน (กดติดตั้งได้ในแท็บ \"ไมค์\")");
            else
                SetNeutral(lblRnNoise, "ตัวตัดเสียงรบกวนในตัว: " + (mode == 1 ? "ไม่ได้ใช้ (ใช้ NVIDIA Broadcast แทน)" : "ปิดอยู่"));
            if (mode == 1)
                SetStatus(lblBroadcast, broadcast, "พบไมค์ NVIDIA Broadcast", "ยังไม่พบ NVIDIA Broadcast (เลือกใช้ไว้ในแท็บ \"ไมค์\")");
            else
                SetNeutral(lblBroadcast, "NVIDIA Broadcast: " + (broadcast ? "ติดตั้งอยู่ (ไม่จำเป็นถ้าใช้ตัวตัดเสียงรบกวนในตัว)" : "ไม่ได้ติดตั้ง (ไม่จำเป็น)"));
        }

        static void SetStatus(Label l, bool ok, string okText, string badText)
        {
            l.Text = (ok ? "✔  " : "✘  ") + (ok ? okText : badText);
            l.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
        }

        static void SetNeutral(Label l, string text)
        {
            l.Text = "•  " + text;
            l.ForeColor = Color.DimGray;
        }

        string RnNoiseDll()
        {
            int rate, ch;
            if (!MicFormat.TryGet(out rate, out ch)) ch = 2;
            return RnNoise.FindDll(settings.Get("rn_dir", RnNoise.DefaultDir()), ch);
        }

        // ------------------------------------------------------------ tab: headphone

        TabPage BuildHeadphoneTab()
        {
            var page = new TabPage("หูฟัง") { Padding = new Padding(16) };
            int y = 16;
            page.Controls.Add(new Label { Text = "ค้นหารุ่นหูฟัง (ฐานข้อมูล AutoEq กว่า 8,000 รุ่น):", Location = new Point(16, y), AutoSize = true });
            y += 28;
            txtSearch = new TextBox { Location = new Point(18, y), Width = 420, Text = "EarPods" };
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };
            var btnSearch = new Button { Text = "ค้นหา", Location = new Point(446, y - 2), Size = new Size(90, 30) };
            btnSearch.Click += delegate { DoSearch(); };
            page.Controls.Add(txtSearch);
            page.Controls.Add(btnSearch);
            y += 36;

            lstHeadphones = new ListBox { Location = new Point(18, y), Size = new Size(518, 170), IntegralHeight = false };
            lstHeadphones.Items.Add("กำลังโหลดฐานข้อมูล...");
            lstHeadphones.DoubleClick += delegate { UseSelectedProfile(); };
            page.Controls.Add(lstHeadphones);

            var btnUse = new Button { Text = "ใช้โปรไฟล์นี้", Location = new Point(548, y), Size = new Size(150, 36), Font = boldFont };
            btnUse.Click += delegate { UseSelectedProfile(); };
            page.Controls.Add(btnUse);
            page.Controls.Add(new Label
            {
                Text = "แนะนำให้เลือกผลวัดจาก\r\nRtings หรือ oratory1990\r\nถ้ามีให้เลือก",
                Location = new Point(548, y + 46), AutoSize = true, ForeColor = Color.DimGray
            });
            y += 180;

            lblProfile = new Label { Location = new Point(18, y), AutoSize = true, Font = boldFont };
            page.Controls.Add(lblProfile);
            y += 30;

            chkHp = new CheckBox { Text = "เปิด EQ หูฟัง (ติ๊กออกเพื่อฟังเทียบเสียงก่อนและหลัง)", Location = new Point(18, y), AutoSize = true };
            chkHp.CheckedChanged += delegate { OnChanged(); };
            page.Controls.Add(chkHp);
            y += 34;

            sBass = AddSlider(page, "เบส (ปรับเพิ่มตามชอบ)", ref y, -6, 6, v => (v > 0 ? "+" : "") + v + " dB");
            sTreble = AddSlider(page, "เสียงแหลม", ref y, -6, 6, v => (v > 0 ? "+" : "") + v + " dB");

            txtProfilePreview = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f),
                Location = new Point(18, y), Size = new Size(760, 90), BackColor = SystemColors.Window,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            page.Controls.Add(txtProfilePreview);
            return page;
        }

        void LoadIndexAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var idx = AutoEq.LoadIndex();
                    BeginInvoke((Action)delegate { index = idx; DoSearch(); });
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)delegate
                    {
                        lstHeadphones.Items.Clear();
                        lstHeadphones.Items.Add("โหลดฐานข้อมูลไม่ได้: " + ex.Message);
                    });
                }
            });
        }

        void DoSearch()
        {
            if (index == null) return;
            var words = txtSearch.Text.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = index.Where(e => words.All(w => e.Name.ToLowerInvariant().Contains(w))).Take(400).ToList();
            lstHeadphones.BeginUpdate();
            lstHeadphones.Items.Clear();
            foreach (var h in hits) lstHeadphones.Items.Add(h);
            if (hits.Count == 0) lstHeadphones.Items.Add("ไม่พบรุ่นนี้ ลองพิมพ์แค่ยี่ห้อหรือรุ่นสั้นๆ");
            lstHeadphones.EndUpdate();
            int best = hits.FindIndex(h => h.Source.StartsWith("Rtings") || h.Source.StartsWith("oratory1990"));
            if (hits.Count > 0) lstHeadphones.SelectedIndex = best >= 0 ? best : 0;
        }

        void UseSelectedProfile()
        {
            var entry = lstHeadphones.SelectedItem as AutoEqEntry;
            if (entry == null) return;
            Cursor = Cursors.WaitCursor;
            lblStatus.Text = "กำลังดาวน์โหลดโปรไฟล์ " + entry.Name + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var text = AutoEq.Download(entry.ProfileUrl);
                    if (!text.Contains("Filter")) throw new Exception("ไฟล์โปรไฟล์ไม่ถูกต้อง");
                    BeginInvoke((Action)delegate
                    {
                        Directory.CreateDirectory(Settings.Dir);
                        File.WriteAllText(Settings.ProfilePath, text, Encoding.UTF8);
                        settings.Set("hp_name", entry.Name);
                        settings.Set("hp_source", entry.Source);
                        chkHp.Checked = true;
                        UpdateProfileUi();
                        Cursor = Cursors.Default;
                        if (!Apply(false)) lblStatus.Text = "ได้โปรไฟล์แล้ว กด \"บันทึกและใช้งาน\" เพื่อเริ่มใช้";
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)delegate
                    {
                        Cursor = Cursors.Default;
                        lblStatus.Text = "ดาวน์โหลดโปรไฟล์ไม่สำเร็จ: " + ex.Message;
                    });
                }
            });
        }

        void UpdateProfileUi()
        {
            bool has = File.Exists(Settings.ProfilePath);
            lblProfile.Text = has
                ? "โปรไฟล์ปัจจุบัน: " + settings.Get("hp_name", "?") + "  (" + settings.Get("hp_source", "") + ")"
                : "ยังไม่ได้เลือกโปรไฟล์หูฟัง";
            txtProfilePreview.Text = has ? File.ReadAllText(Settings.ProfilePath).Replace("\n", "\r\n").Replace("\r\r", "\r") : "";
        }

        // ------------------------------------------------------------ tab: mic

        class MicPreset
        {
            public string Name; public bool On; public int Hp, Mud, Presence, Lp, Gain;
            public override string ToString() { return Name; }
        }

        static readonly MicPreset[] Presets =
        {
            new MicPreset { Name = "เสียงพูดชัด (แนะนำ)", On = true, Hp = 90, Mud = -2, Presence = 3, Lp = 15, Gain = 0 },
            new MicPreset { Name = "คมชัดสูงสุด (สื่อสารในเกม)", On = true, Hp = 120, Mud = -3, Presence = 5, Lp = 12, Gain = 0 },
            new MicPreset { Name = "เป็นธรรมชาติ (คุย/สตรีม)", On = true, Hp = 70, Mud = -1, Presence = 2, Lp = 18, Gain = 0 },
            new MicPreset { Name = "ปิด (เสียงดิบ)", On = false, Hp = 90, Mud = 0, Presence = 0, Lp = 20, Gain = 0 },
        };

        TabPage BuildMicTab()
        {
            var page = new TabPage("ไมค์") { Padding = new Padding(16), AutoScroll = true };
            int y = 10;

            // ---- noise suppression
            page.Controls.Add(new Label { Text = "ตัดเสียงรบกวน", Font = boldFont, ForeColor = Color.SteelBlue, Location = new Point(16, y), AutoSize = true });
            y += 28;
            page.Controls.Add(new Label { Text = "ระบบที่ใช้:", Location = new Point(18, y + 3), AutoSize = true });
            cmbNoise = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(120, y), Width = 470 };
            cmbNoise.Items.AddRange(new object[]
            {
                "AI ในตัว G3 Tuner (RNNoise ทำงานบน CPU)",
                "NVIDIA Broadcast (AI บน GPU ตั้งค่าในโปรแกรมของ NVIDIA)",
                "ปิด"
            });
            cmbNoise.SelectedIndexChanged += delegate
            {
                if (loading) return;
                settings.Set("nr_mode", cmbNoise.SelectedIndex);
                UpdateNoiseUi();
                OnChanged();
            };
            page.Controls.Add(cmbNoise);
            y += 38;

            lblRnStatus = new Label { Location = new Point(18, y + 6), Size = new Size(318, 24), Font = boldFont };
            btnRnInstall = new Button { Text = "ติดตั้งตัวตัดเสียงรบกวน", Location = new Point(340, y), Size = new Size(210, 32) };
            btnRnInstall.Click += delegate { InstallRnNoise(); };
            btnRnBrowse = new Button { Text = "เลือกไฟล์เอง...", Location = new Point(558, y), Size = new Size(130, 32) };
            btnRnBrowse.Click += delegate { BrowseRnNoise(); };
            page.Controls.Add(lblRnStatus);
            page.Controls.Add(btnRnInstall);
            page.Controls.Add(btnRnBrowse);
            y += 36;
            prgInstall = new ProgressBar { Location = new Point(18, y), Size = new Size(670, 6), Visible = false };
            page.Controls.Add(prgInstall);
            y += 6;
            lblMicFormat = new Label { Location = new Point(18, y), Size = new Size(770, 22), ForeColor = Color.DimGray };
            page.Controls.Add(lblMicFormat);
            y += 26;

            sVad = AddSlider(page, "ปิดเสียงอัตโนมัติตอนไม่พูด", ref y, 0, 19, v => v == 0 ? "ไม่ตัด" : (v * 5) + "%");
            sGrace = AddSlider(page, "หน่วงก่อนปิด (กันท้ายคำขาด)", ref y, 0, 20, v => (v * 50) + " ms");
            lblNoiseNote = new Label { Location = new Point(18, y), Size = new Size(770, 40), ForeColor = Color.DimGray };
            page.Controls.Add(lblNoiseNote);
            y += 48;

            // ---- tone EQ
            page.Controls.Add(new Label { Text = "ปรับเสียงไมค์ (EQ)", Font = boldFont, ForeColor = Color.SteelBlue, Location = new Point(16, y + 3), AutoSize = true });
            page.Controls.Add(new Label { Text = "พรีเซ็ต:", Location = new Point(250, y + 3), AutoSize = true });
            cmbPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(320, y), Width = 300 };
            foreach (var p in Presets) cmbPreset.Items.Add(p);
            cmbPreset.SelectedIndexChanged += delegate
            {
                if (loading) return;
                var p = (MicPreset)cmbPreset.SelectedItem;
                loading = true;
                chkMic.Checked = p.On; sHp.V = p.Hp / 10; sMud.V = p.Mud; sPresence.V = p.Presence; sLp.V = p.Lp; sGain.V = p.Gain;
                loading = false;
                OnChanged();
            };
            page.Controls.Add(cmbPreset);
            y += 36;

            chkMic = new CheckBox { Text = "เปิด EQ ไมค์ของ G3", Location = new Point(18, y), AutoSize = true };
            chkMic.CheckedChanged += delegate { OnChanged(); };
            page.Controls.Add(chkMic);
            y += 32;

            sHp = AddSlider(page, "ตัดเสียงหึ่ง ลม และเสียงกระแทกย่านต่ำ", ref y, 4, 20, v => (v * 10) + " Hz", 10);
            sMud = AddSlider(page, "ลดเสียงอู้และก้อง (250 Hz)", ref y, -6, 0, v => v + " dB");
            sPresence = AddSlider(page, "เพิ่มความคมชัดของเสียงพูด (4 kHz)", ref y, 0, 8, v => "+" + v + " dB");
            sLp = AddSlider(page, "ตัดเสียงซ่าย่านสูง", ref y, 8, 20, v => v >= 20 ? "ปิด" : "เหนือ " + v + " kHz");
            sGain = AddSlider(page, "ความดังไมค์", ref y, -6, 12, v => (v > 0 ? "+" : "") + v + " dB");

            page.Controls.Add(new Label
            {
                Location = new Point(18, y + 4), Size = new Size(770, 66), ForeColor = Color.DimGray,
                Text = "ลำดับการประมวลผล: ไมค์ G3 → ตัดเสียงต่ำ → AI ตัดเสียงรบกวน → EQ เพิ่มความคม → เกม/Discord\r\n" +
                       "• ถ้าเสียงยังซ่า ให้ลดระดับไมค์ใน Windows ก่อน ได้ผลกว่าการตัดย่านสูงเพิ่ม\r\n" +
                       "• \"ความคมชัด\" ที่เกิน +5 dB อาจทำให้เสียง ส/ซ แหลมเกินไป"
            });
            return page;
        }

        void UpdateNoiseUi()
        {
            int mode = cmbNoise.SelectedIndex;
            bool inApp = mode == 0;
            var dll = RnNoiseDll();
            lblRnStatus.Visible = btnRnInstall.Visible = btnRnBrowse.Visible = inApp;
            sVad.Bar.Enabled = sGrace.Bar.Enabled = inApp;
            if (dll != null)
            {
                lblRnStatus.Text = "✔  ติดตั้งแล้ว (" + Path.GetFileName(dll) + ")";
                lblRnStatus.ForeColor = Color.SeaGreen;
                btnRnInstall.Text = "ติดตั้งใหม่";
            }
            else
            {
                lblRnStatus.Text = "✘  ยังไม่ได้ติดตั้ง";
                lblRnStatus.ForeColor = Color.Firebrick;
                btnRnInstall.Text = "ติดตั้งตัวตัดเสียงรบกวน";
            }

            int rate, ch;
            if (!MicFormat.TryGet(out rate, out ch))
                lblMicFormat.Text = "อ่านรูปแบบเสียงของไมค์ G3 ไม่ได้ (ตรวจว่าเสียบ G3 อยู่)";
            else
                lblMicFormat.Text = "รูปแบบเสียงไมค์ G3 ใน Windows: " + rate + " Hz, " + ch + " ช่อง" +
                    (rate == 48000 ? "   ✔ เหมาะกับ AI ตัดเสียงรบกวน"
                                   : "   ⚠ ควรตั้งเป็น 48000 Hz (ตั้งค่าไมค์ใน Windows → Properties → Advanced)");

            lblNoiseNote.Text = mode == 0
                ? "ในเกมหรือ Discord เลือกไมค์ \"Microphone (Sound Blaster G3)\" ได้เลย  •  \"ไม่ตัด\" = ลดเสียงซ่าอย่างเดียว ไม่ปิดไมค์ตอนเงียบ\r\n" +
                  "ถ้าเปิด NVIDIA Broadcast อยู่ด้วย ให้ปิด Noise Removal ฝั่งนั้น จะได้ไม่ตัดเสียงซ้ำสองรอบ"
                : mode == 1
                    ? "เปิด Noise Removal และ Room Echo Removal ในโปรแกรม NVIDIA Broadcast แล้วเลือกไมค์ \"Microphone (NVIDIA Broadcast)\" ในเกมหรือ Discord"
                    : "ไม่ตัดเสียงรบกวน (ใช้แค่ EQ ด้านล่าง)";
            if (lblRnNoise != null) RefreshStatus();
        }

        void InstallRnNoise()
        {
            var dir = RnNoise.DefaultDir();
            if (dir == null)
            {
                MessageBox.Show(this, "ต้องติดตั้ง Equalizer APO ก่อน (ดูขั้นตอนในแท็บ \"เริ่มต้น\")", Text);
                return;
            }
            if (!CanWrite(dir))
            {
                var elevate = MessageBox.Show(this,
                    "การติดตั้งต้องวางไฟล์ไว้ในโฟลเดอร์ของ Equalizer APO ซึ่งต้องใช้สิทธิ์ Administrator\r\n" +
                    "จะเปิด G3 Tuner ใหม่แบบ Administrator แล้วติดตั้งต่อให้เลยไหม?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (elevate == DialogResult.Yes && Run(Application.ExecutablePath, "--install-rnnoise", true)) Close();
                return;
            }
            var ok = MessageBox.Show(this,
                "จะดาวน์โหลด win-rnnoise.zip (ประมาณ 36 MB) จาก GitHub ของโปรเจกต์\r\n" +
                "werman/noise-suppression-for-voice (โอเพนซอร์ส GPL-3.0)\r\n" +
                "แล้วติดตั้งเฉพาะไฟล์ปลั๊กอินที่ต้องใช้ไว้ที่\r\n" + dir + "\r\n\r\nดำเนินการต่อไหม?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok != DialogResult.Yes) return;

            var tmp = Path.Combine(Path.GetTempPath(), "g3tuner-rnnoise.zip");
            btnRnInstall.Enabled = false;
            prgInstall.Value = 0;
            prgInstall.Visible = true;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "G3Tuner";
            wc.DownloadProgressChanged += (s, e) =>
            {
                prgInstall.Value = e.ProgressPercentage;
                lblRnStatus.Text = "กำลังดาวน์โหลด " + e.ProgressPercentage + "%";
            };
            wc.DownloadFileCompleted += (s, e) =>
            {
                wc.Dispose();
                prgInstall.Visible = false;
                btnRnInstall.Enabled = true;
                try
                {
                    if (e.Error != null) throw e.Error;
                    RnNoise.ExtractVst2(tmp, dir);
                    settings.Set("rn_dir", dir);
                    loading = true;
                    cmbNoise.SelectedIndex = 0;
                    loading = false;
                    UpdateNoiseUi();
                    lblStatus.Text = Apply(false)
                        ? "ติดตั้งและเปิดใช้ตัวตัดเสียงรบกวนแล้ว"
                        : "ติดตั้งตัวตัดเสียงรบกวนแล้ว กด \"บันทึกและใช้งาน\" เพื่อเริ่มใช้";
                }
                catch (Exception ex)
                {
                    UpdateNoiseUi();
                    MessageBox.Show(this, "ติดตั้งไม่สำเร็จ: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            };
            wc.DownloadFileAsync(new Uri(RnNoise.ZipUrl), tmp);
        }

        void BrowseRnNoise()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "เลือก rnnoise_stereo.dll หรือ rnnoise_mono.dll (VST2 แบบ 64-bit)",
                Filter = "RNNoise VST2 (rnnoise_*.dll)|rnnoise_mono.dll;rnnoise_stereo.dll"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                settings.Set("rn_dir", Path.GetDirectoryName(dlg.FileName));
                UpdateNoiseUi();
                OnChanged();
                var inst = EqApo.InstallDir();
                if (inst == null || !dlg.FileName.StartsWith(inst, StringComparison.OrdinalIgnoreCase))
                    MessageBox.Show(this,
                        "แนะนำให้วางไฟล์ไว้ในโฟลเดอร์ VSTPlugins ของ Equalizer APO\r\n" +
                        "เพราะระบบเสียงของ Windows อาจไม่มีสิทธิ์อ่านไฟล์ในโฟลเดอร์ส่วนตัว (เช่น Downloads)", Text);
            }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".g3tuner-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        Slider AddSlider(Control parent, string title, ref int y, int min, int max, Func<int, string> fmt, int ticks = 1)
        {
            parent.Controls.Add(new Label { Text = title, Location = new Point(18, y + 6), Size = new Size(300, 24) });
            var bar = new TrackBar
            {
                Minimum = min, Maximum = max, TickFrequency = ticks == 1 ? 1 : 2, SmallChange = 1, LargeChange = 2,
                Location = new Point(320, y), Width = 340, AutoSize = false, Height = 36
            };
            var val = new Label { Location = new Point(668, y + 6), Size = new Size(120, 24), Font = boldFont };
            var s = new Slider { Bar = bar, Value = val, Format = fmt };
            bar.ValueChanged += delegate { val.Text = fmt(bar.Value); OnChanged(); };
            val.Text = fmt(bar.Value);
            parent.Controls.Add(bar);
            parent.Controls.Add(val);
            y += 44;
            return s;
        }

        // ------------------------------------------------------------ tab: test

        TabPage BuildTestTab()
        {
            var page = new TabPage("ทดสอบไมค์") { Padding = new Padding(16) };
            int y = 16;
            page.Controls.Add(new Label
            {
                Text = "อัดเสียงแล้ววัดผลว่าเสียงชัดแค่ไหน และมีเสียงซ่าเท่าไร พร้อมเปิดฟังเทียบ A กับ B ได้\r\n" +
                       "วิธีอัด: กดปุ่มแล้วพูดประโยคปกติ 3 วินาที จากนั้นเงียบ 3 วินาที (ช่วงเงียบใช้วัดเสียงซ่า)",
                Location = new Point(16, y), Size = new Size(780, 48)
            });
            y += 54;

            chkCompare = new CheckBox
            {
                Text = "เทียบปิด/เปิดตัวตัดเสียงรบกวนของ G3 Tuner (อัด 2 รอบ ให้พูดประโยคเดิมทั้งสองรอบ)",
                Location = new Point(18, y), AutoSize = true
            };
            chkCompare.CheckedChanged += delegate { UpdateCompareUi(); };
            page.Controls.Add(chkCompare);
            y += 34;

            page.Controls.Add(new Label { Text = "ไมค์ A:", Location = new Point(16, y + 3), AutoSize = true });
            cmbDevA = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(200, y), Width = 360 };
            page.Controls.Add(cmbDevA);
            y += 36;
            page.Controls.Add(new Label { Text = "ไมค์ B (NVIDIA Broadcast):", Location = new Point(16, y + 3), AutoSize = true });
            cmbDevB = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(200, y), Width = 360 };
            page.Controls.Add(cmbDevB);
            var btnReload = new Button { Text = "รีเฟรช", Location = new Point(570, y - 37), Size = new Size(90, 30) };
            btnReload.Click += delegate { RefreshDevices(); };
            page.Controls.Add(btnReload);
            y += 46;

            btnRecord = new Button { Text = "อัดทดสอบ 6 วินาที", Location = new Point(18, y), Size = new Size(200, 40), Font = boldFont };
            btnRecord.Click += delegate { StartTest(); };
            prgRecord = new ProgressBar { Location = new Point(230, y + 8), Size = new Size(430, 24), Maximum = 100 };
            page.Controls.Add(btnRecord);
            page.Controls.Add(prgRecord);
            y += 56;

            lblTitleA = new Label { Location = new Point(16, y), AutoSize = true, Font = boldFont };
            page.Controls.Add(lblTitleA);
            btnPlayA = new Button { Text = "ฟัง A", Location = new Point(680, y - 4), Size = new Size(90, 30), Enabled = false };
            btnPlayA.Click += delegate { Play("A"); };
            page.Controls.Add(btnPlayA);
            y += 28;
            lblResA = new Label { Location = new Point(18, y), Size = new Size(760, 52), Text = "-" };
            page.Controls.Add(lblResA);
            y += 62;

            lblTitleB = new Label { Location = new Point(16, y), AutoSize = true, Font = boldFont };
            page.Controls.Add(lblTitleB);
            btnPlayB = new Button { Text = "ฟัง B", Location = new Point(680, y - 4), Size = new Size(90, 30), Enabled = false };
            btnPlayB.Click += delegate { Play("B"); };
            page.Controls.Add(btnPlayB);
            y += 28;
            lblResB = new Label { Location = new Point(18, y), Size = new Size(760, 52), Text = "-" };
            page.Controls.Add(lblResB);
            y += 62;

            var btnStop = new Button { Text = "หยุดเล่น", Location = new Point(680, y), Size = new Size(90, 30) };
            btnStop.Click += delegate { if (player != null) player.Stop(); };
            page.Controls.Add(btnStop);
            page.Controls.Add(new Label
            {
                Text = "dBFS = ระดับเสียงเทียบกับเสียงดังสุด (0)   •   SNR คือเสียงพูดดังกว่าเสียงซ่ากี่ dB ยิ่งสูงยิ่งชัด (40 ขึ้นไปถือว่าดี)",
                Location = new Point(18, y + 4), Size = new Size(650, 44), ForeColor = Color.DimGray
            });
            return page;
        }

        void RefreshDevices()
        {
            if (cmbDevA == null) return;
            var devs = Audio.InputDevices();
            cmbDevA.Items.Clear(); cmbDevB.Items.Clear();
            cmbDevB.Items.Add(new DevItem { Index = -1, Name = "(ไม่อัด)" });
            foreach (var d in devs) { cmbDevA.Items.Add(d); cmbDevB.Items.Add(d); }
            SelectDevice(cmbDevA, d => d.Name.Contains("Sound Blaster G3") && d.Name.StartsWith("Microphone"));
            SelectDevice(cmbDevB, d => d.Name.Contains("NVIDIA Broadcast"));
            if (cmbDevA.SelectedIndex < 0 && cmbDevA.Items.Count > 0) cmbDevA.SelectedIndex = 0;
            if (cmbDevB.SelectedIndex < 0) cmbDevB.SelectedIndex = 0;
        }

        static void SelectDevice(ComboBox c, Func<DevItem, bool> pred)
        {
            for (int i = 0; i < c.Items.Count; i++)
                if (((DevItem)c.Items[i]).Index >= 0 && pred((DevItem)c.Items[i])) { c.SelectedIndex = i; return; }
        }

        void UpdateCompareUi()
        {
            bool cmp = chkCompare.Checked;
            cmbDevB.Enabled = !cmp;
            lblTitleA.Text = cmp ? "ผล A (ปิดตัวตัดเสียงรบกวน)" : "ผล A (ไมค์ A)";
            lblTitleB.Text = cmp ? "ผล B (เปิดตัวตัดเสียงรบกวน)" : "ผล B (ไมค์ B / หลังผ่าน NVIDIA Broadcast)";
            btnRecord.Text = cmp ? "อัดเทียบ 2 รอบ" : "อัดทดสอบ 6 วินาที";
        }

        void StartTest()
        {
            var a = cmbDevA.SelectedItem as DevItem;
            var b = cmbDevB.SelectedItem as DevItem;
            if (a == null) { MessageBox.Show(this, "ไม่พบไมค์ในเครื่อง", Text); return; }
            if (chkCompare.Checked) { StartCompareTest(a); return; }
            if (b != null && b.Index == a.Index) b = null;
            if (player != null) player.Stop();
            btnRecord.Enabled = btnPlayA.Enabled = btnPlayB.Enabled = false;
            lblResA.Text = "กำลังอัด... พูดเลยครับ"; lblResB.Text = b == null || b.Index < 0 ? "-" : "กำลังอัด...";
            const int secs = 6;

            ThreadPool.QueueUserWorkItem(delegate
            {
                short[] ra = null, rb = null;
                string errA = null, errB = null;
                Thread tb = null;
                if (b != null && b.Index >= 0)
                {
                    tb = new Thread(() => { try { rb = Audio.Record(b.Index, secs, null); } catch (Exception ex) { errB = ex.Message; } });
                    tb.Start();
                }
                try
                {
                    ra = Audio.Record(a.Index, secs, p => BeginInvoke((Action)delegate { prgRecord.Value = (int)(p * 100); }));
                }
                catch (Exception ex) { errA = ex.Message; }
                if (tb != null) tb.Join();

                BeginInvoke((Action)delegate
                {
                    prgRecord.Value = 100;
                    btnRecord.Enabled = true;
                    ShowResult(ra, errA, "A", lblResA, btnPlayA);
                    if (b != null && b.Index >= 0) ShowResult(rb, errB, "B", lblResB, btnPlayB);
                });
            });
        }

        // Records the same mic twice: once with RNNoise bypassed, once with it on (Equalizer APO reloads config live).
        void StartCompareTest(DevItem mic)
        {
            if (cmbNoise.SelectedIndex != 0 || RnNoiseDll() == null)
            {
                MessageBox.Show(this, "ต้องเลือกระบบ \"AI ในตัว G3 Tuner\" และติดตั้งตัวตัดเสียงรบกวนในแท็บ \"ไมค์\" ก่อนครับ", Text);
                return;
            }
            SaveUiToSettings();
            if (!WriteConfig(true))
            {
                MessageBox.Show(this, "กด \"บันทึกและใช้งาน\" ด้านล่างก่อน 1 ครั้งครับ", Text);
                return;
            }
            if (player != null) player.Stop();
            btnRecord.Enabled = btnPlayA.Enabled = btnPlayB.Enabled = false;
            lblResA.Text = "รอบ 1 (ปิดตัวตัดเสียงรบกวน): เตรียมพูด...";
            lblResB.Text = "รอ...";
            const int secs = 6;

            ThreadPool.QueueUserWorkItem(delegate
            {
                short[] ra = null, rb = null;
                string err = null;
                try
                {
                    Thread.Sleep(800); // let Equalizer APO pick up the new config
                    BeginInvoke((Action)delegate { lblResA.Text = "รอบ 1 (ปิดตัวตัดเสียงรบกวน): พูดได้เลย แล้วเงียบ 3 วินาที"; });
                    ra = Audio.Record(mic.Index, secs, p => BeginInvoke((Action)delegate { prgRecord.Value = (int)(p * 50); }));
                    Invoke((Action)delegate
                    {
                        WriteConfig(false);
                        lblResA.Text = "อัดรอบ 1 เสร็จแล้ว";
                        lblResB.Text = "รอบ 2 (เปิดตัวตัดเสียงรบกวน): เตรียมพูดประโยคเดิม...";
                    });
                    Thread.Sleep(800);
                    BeginInvoke((Action)delegate { lblResB.Text = "รอบ 2 (เปิดตัวตัดเสียงรบกวน): พูดประโยคเดิมได้เลย แล้วเงียบ 3 วินาที"; });
                    rb = Audio.Record(mic.Index, secs, p => BeginInvoke((Action)delegate { prgRecord.Value = 50 + (int)(p * 50); }));
                }
                catch (Exception ex) { err = ex.Message; }

                BeginInvoke((Action)delegate
                {
                    WriteConfig(false); // always restore the real settings
                    prgRecord.Value = 100;
                    btnRecord.Enabled = true;
                    ShowResult(ra, err, "A", lblResA, btnPlayA);
                    ShowResult(rb, err, "B", lblResB, btnPlayB);
                });
            });
        }

        void ShowResult(short[] rec, string err, string tag, Label lbl, Button play)
        {
            if (rec == null) { lbl.Text = "อัดไม่ได้: " + err; return; }
            Directory.CreateDirectory(Settings.Dir);
            Audio.WriteWav(Path.Combine(Settings.Dir, "test_" + tag + ".wav"), rec);
            lbl.Text = Analysis.Of(rec).Describe();
            play.Enabled = true;
        }

        void Play(string tag)
        {
            var path = Path.Combine(Settings.Dir, "test_" + tag + ".wav");
            if (!File.Exists(path)) return;
            if (player != null) player.Stop();
            player = new SoundPlayer(path);
            player.Play();
        }

        // ------------------------------------------------------------ settings <-> UI, apply

        void LoadSettingsToUi()
        {
            loading = true;
            chkHp.Checked = settings.GetBool("hp_enabled", true);
            sBass.V = settings.GetInt("hp_bass", 0);
            sTreble.V = settings.GetInt("hp_treble", 0);
            chkMic.Checked = settings.GetBool("mic_enabled", true);
            sHp.V = settings.GetInt("mic_hp", 90) / 10;
            sMud.V = settings.GetInt("mic_mud", -2);
            sPresence.V = settings.GetInt("mic_presence", 3);
            sLp.V = settings.GetInt("mic_lp", 15);
            sGain.V = settings.GetInt("mic_gain", 0);
            int preset = settings.GetInt("mic_preset", 0);
            cmbPreset.SelectedIndex = preset >= 0 && preset < Presets.Length ? preset : 0;
            int mode = settings.GetInt("nr_mode", 0);
            cmbNoise.SelectedIndex = mode >= 0 && mode <= 2 ? mode : 0;
            sVad.V = settings.GetInt("nr_vad", 60) / 5;
            sGrace.V = settings.GetInt("nr_grace", 200) / 50;
            chkCompare.Checked = cmbNoise.SelectedIndex == 0;
            loading = false;
            UpdateProfileUi();
            UpdateNoiseUi();
            UpdateCompareUi();
        }

        void SaveUiToSettings()
        {
            settings.Set("hp_enabled", chkHp.Checked);
            settings.Set("hp_bass", sBass.V);
            settings.Set("hp_treble", sTreble.V);
            settings.Set("mic_enabled", chkMic.Checked);
            settings.Set("mic_hp", sHp.V * 10);
            settings.Set("mic_mud", sMud.V);
            settings.Set("mic_presence", sPresence.V);
            settings.Set("mic_lp", sLp.V);
            settings.Set("mic_gain", sGain.V);
            settings.Set("mic_preset", cmbPreset.SelectedIndex);
            settings.Set("nr_mode", cmbNoise.SelectedIndex);
            settings.Set("nr_vad", sVad.V * 5);
            settings.Set("nr_grace", sGrace.V * 50);
            settings.Save();
        }

        void OnChanged()
        {
            if (loading || lblStatus == null) return;
            applyTimer.Stop();
            applyTimer.Start();
        }

        const string IncludeLine = "Include: G3Tuner.txt";

        static bool IsHooked(string configDir)
        {
            var main = Path.Combine(configDir, "config.txt");
            return File.Exists(main) && Regex.IsMatch(File.ReadAllText(main),
                @"^\s*Include:\s*G3Tuner\.txt\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        static string LoadProfile()
        {
            return File.Exists(Settings.ProfilePath) ? File.ReadAllText(Settings.ProfilePath) : null;
        }

        // Rewrites G3Tuner.txt from the saved settings; only once config.txt already includes it.
        bool WriteConfig(bool forceNoiseOff)
        {
            var dir = EqApo.ConfigDir();
            if (dir == null || !IsHooked(dir)) return false;
            try
            {
                File.WriteAllText(Path.Combine(dir, "G3Tuner.txt"),
                    ConfigBuilder.Build(settings, LoadProfile(), forceNoiseOff), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        // interactive = user pressed the button (may ask questions / offer admin restart)
        bool Apply(bool interactive)
        {
            SaveUiToSettings();
            var dir = EqApo.ConfigDir();
            if (dir == null)
            {
                if (interactive)
                    MessageBox.Show(this, "บันทึกการตั้งค่าแล้ว แต่ยังไม่พบ Equalizer APO\r\nติดตั้งตามขั้นตอนในแท็บ \"เริ่มต้น\" ก่อนครับ", Text);
                return false;
            }
            try
            {
                var main = Path.Combine(dir, "config.txt");
                bool hooked = IsHooked(dir);
                if (!hooked && !interactive) return false;

                File.WriteAllText(Path.Combine(dir, "G3Tuner.txt"), ConfigBuilder.Build(settings, LoadProfile(), false), new UTF8Encoding(false));

                if (!hooked)
                {
                    var ans = MessageBox.Show(this,
                        "G3 Tuner จะสำรองไฟล์ config.txt เดิมของ Equalizer APO ไว้เป็น config.txt.g3tuner-backup\r\n" +
                        "แล้วตั้งให้ Equalizer APO ใช้ค่าจาก G3 Tuner แทน\r\n\r\n" +
                        "(ค่าตั้งต้นของ Equalizer APO มี EQ ตัวอย่างที่ลดเสียงลง 6 dB กับทุกอุปกรณ์ จึงควรแทนที่)\r\n\r\nดำเนินการต่อไหม?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes) return false;
                    if (File.Exists(main)) File.Copy(main, main + ".g3tuner-backup", true);
                    File.WriteAllText(main,
                        "# Managed by G3 Tuner. Original file saved as config.txt.g3tuner-backup\r\n" + IncludeLine + "\r\n",
                        new UTF8Encoding(false));
                }
                lblStatus.Text = "ใช้งานแล้ว " + DateTime.Now.ToString("HH:mm:ss") + "  •  ปรับค่าต่อจากนี้จะมีผลทันที";
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                if (!interactive) { lblStatus.Text = "ไม่มีสิทธิ์เขียนไฟล์ของ Equalizer APO"; return false; }
                var ans = MessageBox.Show(this,
                    "Windows ไม่อนุญาตให้เขียนไฟล์ในโฟลเดอร์ของ Equalizer APO\r\nเปิด G3 Tuner ใหม่แบบ Administrator ไหม?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans == DialogResult.Yes && Run(Application.ExecutablePath, null, true)) Close();
                return false;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "บันทึกไม่สำเร็จ: " + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------ misc

        void OpenUrl(string url) { Run(url, null, false); }

        bool Run(string file, string args, bool elevate)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                if (elevate) psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "เปิดไม่ได้: " + ex.Message;
                return false;
            }
        }
    }
}
