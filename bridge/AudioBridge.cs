using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class AudioBridge
{
    private const int Port = 18742, Size = 2048, BandCount = 24;
    private static volatile string frame;
    private static volatile bool captureEnabled;
    private static volatile bool captureInput;
    private static volatile int sourceRevision;
    private static volatile int frameRevision = -1;
    private static readonly AutoResetEvent captureChanged = new AutoResetEvent(false);
    private static readonly CultureInfo Format = CultureInfo.InvariantCulture;

    [MTAThread]
    public static void Main(string[] args)
    {
        captureEnabled = Array.IndexOf(args, "--capture") >= 0;
        frame = Empty(false);
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.ExclusiveAddressUse = true;
        try { listener.Start(); }
        catch (SocketException error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
            return;
        }
        var server = new Thread(() => Serve(listener));
        server.IsBackground = true;
        server.Start();
        Console.WriteLine("Audio Bridge running on http://127.0.0.1:" + Port + "/levels");
        Console.WriteLine("Keep this window open while using the widget. Close it to exit.");

        try
        {
            while (true)
            {
                if (!captureEnabled)
                {
                    captureChanged.WaitOne();
                    continue;
                }
                try { Capture(); }
                catch (Exception error) { Console.WriteLine("Audio device retry: " + error.Message); }
                frame = Empty(false);
                if (captureEnabled) captureChanged.WaitOne(1000);
            }
        }
        finally { listener.Stop(); }
    }

    private static void Serve(TcpListener listener)
    {
        while (true)
        {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = 250;
                    client.SendTimeout = 250;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadRequest(stream);
                        string body = "";
                        string code = "404 Not Found";
                        if (request.StartsWith("GET /levels HTTP/"))
                        {
                            bool enabled = captureEnabled;
                            string snapshot = enabled && frameRevision == sourceRevision ? frame : Empty(false);
                            body = "{\"enabled\":" + (enabled ? "true" : "false") +
                                ",\"source\":\"" + (captureInput ? "input" : "output") + "\"," + snapshot.Substring(1);
                            code = "200 OK";
                        }
                        else if (request.StartsWith("GET /artwork?track="))
                        {
                            string key = request.Split(' ')[1].Substring("/artwork?track=".Length);
                            code = key.Length > 2048 ? "400 Bad Request" :
                                AllowArtworkOrigin(request) ? ReadArtwork(key, out body) : "403 Forbidden";
                        }
                        else if (request.StartsWith("POST /seek?track="))
                        {
                            code = AllowArtworkOrigin(request) &&
                                request.IndexOf("\r\nX-Xeneon-Control: 1\r\n", StringComparison.OrdinalIgnoreCase) >= 0
                                ? QueueSeek(request.Split(' ')[1]) : "403 Forbidden";
                        }
                        else if (request.StartsWith("OPTIONS /capture/") ||
                                 request.StartsWith("OPTIONS /levels HTTP/") || request.StartsWith("OPTIONS /artwork?track=") || request.StartsWith("OPTIONS /seek?track="))
                        {
                            code = "204 No Content";
                        }
                        else if (request.StartsWith("POST /capture/source/input HTTP/") ||
                                 request.StartsWith("POST /capture/source/output HTTP/"))
                        {
                            if (AllowArtworkOrigin(request) &&
                                request.IndexOf("\r\nX-Xeneon-Control: 1\r\n", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                bool input = request.StartsWith("POST /capture/source/input HTTP/");
                                if (input != captureInput)
                                {
                                    captureInput = input;
                                    sourceRevision++;
                                    captureChanged.Set();
                                }
                                code = "204 No Content";
                            }
                            else code = "403 Forbidden";
                        }
                        else if (request.StartsWith("POST /capture/start HTTP/") ||
                                 request.StartsWith("POST /capture/stop HTTP/"))
                        {
                            if (request.IndexOf("\r\nX-Xeneon-Control: 1\r\n", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                captureEnabled = request.StartsWith("POST /capture/start HTTP/");
                                captureChanged.Set();
                                code = "204 No Content";
                            }
                            else code = "403 Forbidden";
                        }
                        byte[] response = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 " + code + "\r\nContent-Type: application/json\r\n" +
                            "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                            "Access-Control-Allow-Headers: X-Xeneon-Control\r\n" +
                            "Access-Control-Max-Age: 600\r\nAccess-Control-Allow-Private-Network: true\r\nCache-Control: no-store\r\n" +
                            "Connection: close\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { if (!listener.Server.IsBound) return; }
        }
    }

    private static readonly object ArtworkLock = new object();
    private static bool artworkReading;
    private static string artworkKey;
    private static string artworkResponse;
    private static DateTime artworkRefreshAt;

    private static string ReadArtwork(string key, out string body)
    {
        body = "";
        lock (ArtworkLock)
        {
            if (key == artworkKey && DateTime.UtcNow < artworkRefreshAt)
            {
                body = artworkResponse;
                return body.Length == 0 ? "204 No Content" : "200 OK";
            }
            if (!artworkReading)
            {
                artworkReading = true;
                ThreadPool.QueueUserWorkItem(_ => RefreshArtwork(key));
            }
        }
        return "202 Accepted";
    }

    private static string QueueSeek(string path)
    {
        const string prefix = "/seek?track=";
        int separator = path.LastIndexOf("&ms=", StringComparison.Ordinal);
        long milliseconds;
        if (separator <= prefix.Length || separator - prefix.Length > 2048 ||
            !long.TryParse(path.Substring(separator + 4), NumberStyles.None, Format, out milliseconds) ||
            milliseconds > 604800000) return "400 Bad Request";
        string key = path.Substring(prefix.Length, separator - prefix.Length);
        lock (ArtworkLock)
        {
            if (artworkReading) return "409 Conflict";
            artworkReading = true;
            artworkRefreshAt = DateTime.MinValue;
            ThreadPool.QueueUserWorkItem(_ => RefreshArtwork(key, milliseconds));
        }
        return "202 Accepted";
    }

    private static void RefreshArtwork(string key, long seekMilliseconds = -1)
    {
        string result = "";
        try
        {
            string powershell = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(key)));
            string parameters = "$seekMilliseconds = " + seekMilliseconds.ToString(Format) +
                "\n$expectedMetadata = '" + expected + "'\n";
            string command = Convert.ToBase64String(Encoding.Unicode.GetBytes(parameters + ArtworkCommand));
            var settings = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + command);
            settings.UseShellExecute = false;
            settings.CreateNoWindow = true;
            settings.RedirectStandardOutput = true;
            settings.RedirectStandardError = true;
            using (Process process = Process.Start(settings))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(9000))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                }
                else if (process.ExitCode == 0 && output.Wait(1000) && errors.Wait(1000))
                {
                    string response = output.Result.Trim();
                    if (response.StartsWith("{") && response.EndsWith("}") && response.Length < 2900000)
                        result = response;
                }
            }
        }
        catch (Exception error) { Console.Error.WriteLine("Artwork: " + error.Message); }
        finally
        {
            lock (ArtworkLock)
            {
                artworkKey = key;
                artworkResponse = result;
                artworkRefreshAt = DateTime.UtcNow.AddSeconds(20);
                artworkReading = false;
            }
        }
    }

    private static bool AllowArtworkOrigin(string request)
    {
        foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (line.Length == 0) break;
            if (!line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) continue;
            string origin = line.Substring(7).Trim();
            Uri uri;
            return origin == "null" || origin == "file://" || origin == "qrc://" ||
                (Uri.TryCreate(origin, UriKind.Absolute, out uri) &&
                 (uri.IsLoopback || uri.Scheme == "icue" || uri.Scheme == "qrc"));
        }
        return true;
    }

    private const string ArtworkCommand = @"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
$propertiesType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime]
$streamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
        $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1

function Await-Result($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    if (-not $task.Wait(2500)) { throw 'Windows media request timed out.' }
    return $task.Result
}

$manager = Await-Result ($managerType::RequestAsync()) $managerType
$session = $manager.GetCurrentSession()
if ($null -eq $session) { exit }
$properties = Await-Result ($session.TryGetMediaPropertiesAsync()) $propertiesType
if ($null -eq $properties -or !$properties.Title) { exit }
$timeline = $session.GetTimelineProperties()
$playback = $session.GetPlaybackInfo()
$start = [long]$timeline.StartTime.TotalMilliseconds
$end = [long]$timeline.EndTime.TotalMilliseconds
$position = [long]$timeline.Position.TotalMilliseconds
$playing = $playback.PlaybackStatus.ToString() -eq 'Playing'
$lastUpdated = $timeline.LastUpdatedTime.ToUnixTimeMilliseconds()
$canSeek = $end -gt $start -and $playback.Controls.IsPlaybackPositionEnabled
$seekApplied = $null
if ($seekMilliseconds -ge 0) {
    $expected = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($expectedMetadata)) | ConvertFrom-Json
    $seekApplied = $false
    if ($canSeek -and $expected.title -eq $properties.Title -and
        (!$expected.artist -or !$properties.Artist -or $expected.artist -eq $properties.Artist) -and
        $seekMilliseconds -ge $start -and $seekMilliseconds -le $end) {
        $seekApplied = Await-Result ($session.TryChangePlaybackPositionAsync([long]($seekMilliseconds * 10000))) ([bool])
        if ($seekApplied) {
            $position = $seekMilliseconds
            $lastUpdated = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
    }
}

function Read-Cover {
if ($null -eq $properties.Thumbnail) { return '' }
$source = Await-Result ($properties.Thumbnail.OpenReadAsync()) $streamType
if ($null -eq $source) { return '' }
try {
    $size = [long]$source.Size
    $contentType = [string]$source.ContentType
    if ($size -lt 16 -or $size -gt 2097152) { return '' }
    $stream = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($source.GetInputStreamAt(0))
    try {
        $memory = New-Object System.IO.MemoryStream
        try {
            $stream.CopyTo($memory)
            $bytes = $memory.ToArray()
        } finally { $memory.Dispose() }
    } finally { $stream.Dispose() }
} finally {
    if ($source -is [IDisposable]) { $source.Dispose() }
}

$mime = switch -Regex ($contentType.ToLowerInvariant()) {
    '^image/(png|jpeg|webp|gif)$' { $_ }
    default { '' }
}
if (!$mime) {
    if ($bytes[0] -eq 137 -and $bytes[1] -eq 80 -and $bytes[2] -eq 78 -and $bytes[3] -eq 71) { $mime = 'image/png' }
    elseif ($bytes[0] -eq 255 -and $bytes[1] -eq 216) { $mime = 'image/jpeg' }
}
if (!$mime) { return '' }
return 'data:' + $mime + ';base64,' + [Convert]::ToBase64String($bytes)
}
$cover = ''
try { $cover = Read-Cover } catch {}
[PSCustomObject]@{
    title = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$properties.Title))
    artist = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$properties.Artist))
    artwork = $cover
    timeline = @{ start = $start; end = $end; position = $position; updated = $lastUpdated; playing = $playing; canSeek = $canSeek }
    seekApplied = $seekApplied
} | ConvertTo-Json -Compress
";

    private static string ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[1024];
        var header = new StringBuilder();
        while (header.Length < 8192)
        {
            int count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            header.Append(Encoding.ASCII.GetString(buffer, 0, count));
            string value = header.ToString();
            if (value.Contains("\r\n\r\n")) return value;
        }
        return "";
    }

    private static void Capture()
    {
        int revision = sourceRevision;
        bool input = captureInput;
        int flow = input ? 1 : 0; // eCapture / eRender
        uint streamFlags = input ? 0U : 0x00020000U; // WASAPI loopback applies to output only.
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDevice device = null;
        IAudioClient audio = null;
        ICaptureClient packets = null;
        bool started = false;
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, 1, out device);
            string originalId;
            device.GetId(out originalId);
            Guid audioId = typeof(IAudioClient).GUID;
            object instance;
            device.Activate(ref audioId, 23, IntPtr.Zero, out instance);
            audio = (IAudioClient)instance;

            IntPtr mix;
            audio.GetMixFormat(out mix);
            int channels, sampleRate, stride, bits, tag;
            try
            {
                tag = Marshal.ReadInt16(mix, 0);
                channels = Marshal.ReadInt16(mix, 2);
                sampleRate = Marshal.ReadInt32(mix, 4);
                stride = Marshal.ReadInt16(mix, 12);
                bits = Marshal.ReadInt16(mix, 14);
                if ((tag & 0xffff) == 0xfffe)
                {
                    tag = Marshal.ReadInt32(mix, 24);
                }
                bool supported = (tag == 3 && bits == 32) ||
                    (tag == 1 && (bits == 16 || bits == 24 || bits == 32));
                if (!supported || channels < 1 || stride < channels * (bits / 8))
                    throw new NotSupportedException("Unsupported Windows mix format.");
                audio.Initialize(0, streamFlags, 1000000, 0, mix, IntPtr.Zero);
            }
            finally { Marshal.FreeCoTaskMem(mix); }

            var analyzer = new Analyzer(sampleRate);
            Guid captureId = typeof(ICaptureClient).GUID;
            audio.GetService(ref captureId, out instance);
            packets = (ICaptureClient)instance;
            audio.Start();
            started = true;
            frame = Empty(true);
            frameRevision = revision;
            Console.WriteLine("Capturing the Windows default " + (input ? "input" : "playback") + " device.");
            var clock = Stopwatch.StartNew();
            long lastFrame = 0, lastPacket = 0, nextCheck = 2000;
            byte[] buffer = new byte[0];
            bool publishedSilence = true;
            while (captureEnabled && revision == sourceRevision)
            {
                uint count;
                packets.GetNextPacketSize(out count);
                while (count != 0)
                {
                    IntPtr data;
                    uint flags;
                    ulong devicePosition, position;
                    packets.GetBuffer(out data, out count, out flags, out devicePosition, out position);
                    try
                    {
                        if ((flags & 2) != 0)
                        {
                            for (int i = 0; i < count; i++) analyzer.Add(0, 0);
                        }
                        else
                        {
                            int length = checked((int)count * stride);
                            if (buffer.Length < length) buffer = new byte[length];
                            Marshal.Copy(data, buffer, 0, length);
                            for (int offset = 0; offset < length; offset += stride)
                            {
                                float left = Sample(buffer, offset, bits, tag);
                                float right = channels == 1 ? left :
                                    Sample(buffer, offset + bits / 8, bits, tag);
                                analyzer.Add(left, right);
                            }
                        }
                        lastPacket = clock.ElapsedMilliseconds;
                    }
                    finally { packets.ReleaseBuffer(count); }
                    packets.GetNextPacketSize(out count);
                }

                long now = clock.ElapsedMilliseconds;
                if (now - lastFrame >= 50)
                {
                    if (analyzer.HasSamples)
                    {
                        frame = analyzer.TakeFrame();
                        publishedSilence = false;
                    }
                    else if (!publishedSilence && now - lastPacket >= 150)
                    {
                        analyzer.Reset();
                        frame = Empty(true);
                        publishedSilence = true;
                    }
                    lastFrame = now;
                }
                if (now >= nextCheck)
                {
                    IDevice current;
                    enumerator.GetDefaultAudioEndpoint(flow, 1, out current);
                    try
                    {
                        string currentId;
                        current.GetId(out currentId);
                        if (currentId != originalId) return;
                    }
                    finally { Marshal.ReleaseComObject(current); }
                    nextCheck = now + 2000;
                }
                Thread.Sleep(publishedSilence ? 25 : 12);
            }
        }
        finally
        {
            if (started) try { audio.Stop(); } catch (COMException) { }
            if (packets != null) Marshal.ReleaseComObject(packets);
            if (audio != null) Marshal.ReleaseComObject(audio);
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static float Sample(byte[] data, int offset, int bits, int tag)
    {
        if (tag == 3)
        {
            float sample = BitConverter.ToSingle(data, offset);
            return float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : sample;
        }
        if (bits == 16) return BitConverter.ToInt16(data, offset) / 32768f;
        if (bits == 24)
        {
            int signed = (data[offset] << 8) | (data[offset + 1] << 16) | (data[offset + 2] << 24);
            return signed / 2147483648f;
        }
        // WAVEFORMATEXTENSIBLE PCM valid bits are left-aligned in their container.
        return BitConverter.ToInt32(data, offset) / 2147483648f;
    }

    private static string Empty(bool ready)
    {
        return "{\"ready\":" + (ready ? "true" : "false") +
            ",\"active\":false,\"vu\":[-90,-90],\"bands\":[0" +
            new string('0', BandCount - 1).Replace("0", ",0") + "]}";
    }

    private sealed class Analyzer
    {
        private readonly double[] left = new double[Size], right = new double[Size], window = new double[Size];
        private readonly double[] real = new double[Size], imaginary = new double[Size];
        private readonly int[] starts = new int[BandCount], ends = new int[BandCount];
        private int cursor, count;
        private double leftEnergy, rightEnergy;

        public Analyzer(int rate)
        {
            for (int i = 0; i < Size; i++) window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (Size - 1));
            double high = Math.Min(16000, rate * .48);
            for (int band = 0; band < BandCount; band++)
            {
                starts[band] = Math.Max(1, (int)Math.Floor(60 * Math.Pow(high / 60, band / (double)BandCount) * Size / rate));
                ends[band] = Math.Max(starts[band] + 1, (int)Math.Floor(60 * Math.Pow(high / 60, (band + 1.0) / BandCount) * Size / rate));
            }
        }

        public bool HasSamples { get { return count > 0; } }

        public void Add(float l, float r)
        {
            left[cursor] = l;
            right[cursor] = r;
            cursor = (cursor + 1) % Size;
            leftEnergy += (double)l * l;
            rightEnergy += (double)r * r;
            count++;
        }

        public void Reset()
        {
            Array.Clear(left, 0, Size);
            Array.Clear(right, 0, Size);
            cursor = count = 0;
            leftEnergy = rightEnergy = 0;
        }

        public string TakeFrame()
        {
            double l = Math.Max(-90, 10 * Math.Log10(leftEnergy / count));
            double r = Math.Max(-90, 10 * Math.Log10(rightEnergy / count));
            count = 0;
            leftEnergy = rightEnergy = 0;
            if (Math.Max(l, r) <= -84) return Empty(true);

            var powers = new double[BandCount];
            AnalyzeChannel(left, powers);
            AnalyzeChannel(right, powers);
            var result = new StringBuilder("{\"ready\":true,\"active\":true,\"vu\":[");
            result.Append(Math.Min(l, 0).ToString("0.0", Format)).Append(',')
                .Append(Math.Min(r, 0).ToString("0.0", Format)).Append("],\"bands\":[");
            for (int band = 0; band < BandCount; band++)
            {
                if (band > 0) result.Append(',');
                double db = 10 * Math.Log10(powers[band] * 16 / (Size * Size));
                double height = Math.Max(0, Math.Min(1, (db + 75) / 65));
                result.Append(height.ToString("0.000", Format));
            }
            return result.Append("]}").ToString();
        }

        private void AnalyzeChannel(double[] samples, double[] powers)
        {
            for (int i = 0; i < Size; i++)
            {
                real[i] = samples[(cursor + i) % Size] * window[i];
                imaginary[i] = 0;
            }
            for (int i = 1, j = 0; i < Size; i++)
            {
                int bit = Size >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i >= j) continue;
                double swap = real[i];
                real[i] = real[j];
                real[j] = swap;
            }
            for (int width = 2; width <= Size; width <<= 1)
            {
                int half = width / 2;
                double stepX = Math.Cos(-2 * Math.PI / width);
                double stepY = Math.Sin(-2 * Math.PI / width);
                for (int start = 0; start < Size; start += width)
                {
                    double x = 1, y = 0;
                    for (int i = start; i < start + half; i++)
                    {
                        int j = i + half;
                        double u = real[j] * x - imaginary[j] * y;
                        double v = real[j] * y + imaginary[j] * x;
                        real[j] = real[i] - u;
                        imaginary[j] = imaginary[i] - v;
                        real[i] += u;
                        imaginary[i] += v;
                        double nextX = x * stepX - y * stepY;
                        y = x * stepY + y * stepX;
                        x = nextX;
                    }
                }
            }
            for (int band = 0; band < BandCount; band++)
            {
                for (int bin = starts[band]; bin < ends[band]; bin++)
                    powers[band] = Math.Max(powers[band], real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(ref Guid id, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        void OpenPropertyStore(int mode, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        void IsFormatSupported(int mode, IntPtr format, out IntPtr nearest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}
