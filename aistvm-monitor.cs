// AISTVM ultra-lightweight system monitor agent.
// Serves a read-only JSON snapshot on the Tailscale interface only.
// Build: csc.exe /target:exe /optimize+ /out:aistvm-monitor.exe aistvm-monitor.cs
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Program
{
    private const int DefaultPort = 8787;
    private const int SampleIntervalMs = 4000;
    private static volatile string _snapshot = "{}";
    private static volatile bool _stopping;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value
        {
            get { return ((ulong)High << 32) | Low; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;

        public MemoryStatus()
        {
            Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    private static void Main(string[] args)
    {
        _stopping = false;
        string ip = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = DefaultPort;
        if (args.Length > 1)
        {
            int parsed;
            if (int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
                parsed > 0 && parsed <= 65535)
            {
                port = parsed;
            }
        }

        Thread sampler = new Thread(delegate() { SampleLoop(); });
        sampler.IsBackground = true;
        sampler.Start();

        HttpListener listener = new HttpListener();
        // Bind on every local interface; Windows Firewall below is the actual
        // boundary and allows this port only from the Tailscale CIDR. Binding
        // to the startup IP can stop working if Tailscale refreshes the
        // interface address after the agent starts.
        listener.Prefixes.Add("http://+:" + port.ToString(CultureInfo.InvariantCulture) + "/");
        listener.Start();
        Console.WriteLine("AISTVM monitor listening on " + ip + ":" + port.ToString(CultureInfo.InvariantCulture));

        while (!_stopping)
        {
            HttpListenerContext context = null;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            try
            {
                Handle(context);
            }
            catch
            {
                // A single malformed or disconnected client must not stop the agent.
            }
        }
        listener.Close();
    }

    private static void SampleLoop()
    {
        _snapshot = BuildJson(0.0);
        FileTime idle;
        FileTime kernel;
        FileTime user;
        bool primed = GetSystemTimes(out idle, out kernel, out user);
        ulong previousIdle = primed ? idle.Value : 0;
        ulong previousKernel = primed ? kernel.Value : 0;
        ulong previousUser = primed ? user.Value : 0;
        int samples = 0;

        while (!_stopping)
        {
            Thread.Sleep(SampleIntervalMs);
            if (!GetSystemTimes(out idle, out kernel, out user)) continue;

            ulong currentIdle = idle.Value;
            ulong currentKernel = kernel.Value;
            ulong currentUser = user.Value;
            if (!primed)
            {
                previousIdle = currentIdle;
                previousKernel = currentKernel;
                previousUser = currentUser;
                primed = true;
                _snapshot = BuildJson(0.0);
                continue;
            }

            double cpu = ComputeCpu(previousIdle, previousKernel, previousUser, currentIdle, currentKernel, currentUser);
            previousIdle = currentIdle;
            previousKernel = currentKernel;
            previousUser = currentUser;
            _snapshot = BuildJson(cpu);
            samples++;
            // Long sessions allocate only tiny response buffers. A periodic
            // collect keeps the working set flat without touching the CPU.
            if (samples % 60 == 0) GC.Collect();
        }
    }

    private static double ComputeCpu(
        ulong previousIdle,
        ulong previousKernel,
        ulong previousUser,
        ulong currentIdle,
        ulong currentKernel,
        ulong currentUser)
    {
        ulong idleDelta = currentIdle >= previousIdle ? currentIdle - previousIdle : 0;
        ulong kernelDelta = currentKernel >= previousKernel ? currentKernel - previousKernel : 0;
        ulong userDelta = currentUser >= previousUser ? currentUser - previousUser : 0;
        ulong totalDelta = kernelDelta + userDelta;
        if (totalDelta == 0) return 0.0;
        // Kernel time includes idle time.
        double busy = (double)totalDelta - (double)idleDelta;
        if (busy < 0.0) busy = 0.0;
        double percent = (busy * 100.0) / (double)totalDelta;
        if (percent < 0.0) return 0.0;
        if (percent > 100.0) return 100.0;
        return percent;
    }

    private static string BuildJson(double cpuPercent)
    {
        MemoryStatus memory = new MemoryStatus();
        if (!GlobalMemoryStatusEx(memory))
        {
            memory.TotalPhys = 0;
            memory.AvailPhys = 0;
        }
        double ramTotal = ToGigabytes(memory.TotalPhys);
        double ramFree = ToGigabytes(memory.AvailPhys);
        double ramUsed = ramTotal - ramFree;
        if (ramUsed < 0.0) ramUsed = 0.0;
        double ramPercent = ramTotal > 0.0 ? (ramUsed / ramTotal) * 100.0 : 0.0;

        double diskTotal = 0.0;
        double diskFree = 0.0;
        double diskPercent = 0.0;
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory);
            DriveInfo drive = new DriveInfo(root);
            diskTotal = ToGigabytes((ulong)drive.TotalSize);
            diskFree = ToGigabytes((ulong)drive.AvailableFreeSpace);
            if (diskTotal > 0.0)
                diskPercent = ((diskTotal - diskFree) / diskTotal) * 100.0;
        }
        catch
        {
            // Keep the endpoint alive even if the system drive is temporarily unavailable.
        }

        StringBuilder json = new StringBuilder(224);
        json.Append("{\"cpu_percent\":").Append(Percent(cpuPercent));
        json.Append(",\"ram_used_gb\":").Append(Gigabytes(ramUsed));
        json.Append(",\"ram_total_gb\":").Append(Gigabytes(ramTotal));
        json.Append(",\"ram_percent\":").Append(Percent(ramPercent));
        json.Append(",\"disk_free_gb\":").Append(Gigabytes(diskFree));
        json.Append(",\"disk_total_gb\":").Append(Gigabytes(diskTotal));
        json.Append(",\"disk_percent\":").Append(Percent(diskPercent));
        json.Append(",\"timestamp\":\"").Append(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\"}");
        return json.ToString();
    }

    private static double ToGigabytes(ulong bytes)
    {
        return (double)bytes / 1073741824.0;
    }

    private static string Percent(double value)
    {
        if (value < 0.0) value = 0.0;
        if (value > 100.0) value = 100.0;
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Gigabytes(double value)
    {
        if (value < 0.0) value = 0.0;
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static void Handle(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/metrics")
            {
                byte[] body = Encoding.UTF8.GetBytes(_snapshot);
                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.ContentLength64 = body.Length;
                response.OutputStream.Write(body, 0, body.Length);
            }
            else
            {
                response.StatusCode = 404;
                response.ContentLength64 = 0;
            }
        }
        finally
        {
            response.OutputStream.Close();
            response.Close();
        }
    }
}
