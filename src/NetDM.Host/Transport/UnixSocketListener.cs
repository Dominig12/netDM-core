using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NetDM.Host.Transport;

internal sealed class UnixSocketListener(Socket listener, UnixSocketListener.SocketLock lease) : IDisposable
{
    public Socket Listener { get; } = listener;

    [SupportedOSPlatform("linux")]
    public static UnixSocketListener Open(string path, int backlog)
    {
        var endpoint = new UnixDomainSocketEndPoint(path);
        var lease = AcquireLock(path + ".lock");
        Socket? listener = null;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            RemoveStaleSocket(path, endpoint);
            listener.Bind(endpoint);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            listener.Listen(backlog);
            return new UnixSocketListener(listener, lease);
        }
        catch
        {
            listener?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        // .NET removes the pathname during Socket.Dispose. Keep the lock until it has done so.
        try { Listener.Dispose(); }
        finally { lease.Dispose(); }
    }

    private static SocketLock AcquireLock(string path)
    {
        const int openFlags = 0x2 | 0x40 | 0x800 | 0x20000 | 0x80000; // RDWR, CREAT, NONBLOCK, NOFOLLOW, CLOEXEC
        var fd = OpenFile(path, openFlags, 0x180); // 0600
        if (fd < 0) throw NativeError("Could not open IPC lock");
        var lease = new SocketLock(fd);
        try
        {
            const int emptyPath = 0x1000;
            if (!TryStat(fd, "", emptyPath, out var entry) || entry.Type != RegularFile ||
                entry.UserId != GetEffectiveUserId() || entry.LinkCount != 1)
                throw new IOException("IPC lock must be a regular file owned by the Host user.");
            const int exclusiveNonblocking = 2 | 4;
            if (Flock(fd, exclusiveNonblocking) != 0)
                throw NativeError("Could not acquire IPC listener lock");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void RemoveStaleSocket(string path, UnixDomainSocketEndPoint endpoint)
    {
        const int currentDirectory = -100;
        const int noFollow = 0x100;
        if (!TryStat(currentDirectory, path, noFollow, out var entry)) return;
        if (entry.Type != SocketFile || entry.UserId != GetEffectiveUserId())
            throw new IOException("IPC path is not a socket owned by the Host user.");

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            { Blocking = false };
        try
        {
            probe.Connect(endpoint);
            throw new IOException("IPC path has a live listener.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // A stopped Host leaves a socket entry. Check it is still the one probed.
            if (!TryStat(currentDirectory, path, noFollow, out var current) || !entry.SameFile(current))
                throw new IOException("IPC path changed during the stale socket check.");
            File.Delete(path);
        }
    }

    private const ushort RegularFile = 0x8000, SocketFile = 0xc000;

    private static bool TryStat(int fd, string path, int flags, out Statx entry)
    {
        const uint fields = 0x1 | 0x4 | 0x8 | 0x100; // TYPE, NLINK, UID, INO
        if (Stat(fd, path, flags, fields, out entry) == 0)
        {
            if ((entry.Mask & fields) != fields) throw new IOException("IPC filesystem lacks required metadata.");
            return true;
        }
        if (Marshal.GetLastPInvokeError() == 2) return false; // ENOENT
        throw NativeError("Could not inspect IPC path");
    }

    private static IOException NativeError(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    // Linux UAPI statx has a fixed layout, independent of libc's struct stat layout.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] private ushort _mode;
        [FieldOffset(32)] private ulong _inode;
        [FieldOffset(136)] private uint _deviceMajor;
        [FieldOffset(140)] private uint _deviceMinor;

        public readonly ushort Type => (ushort)(_mode & 0xf000);
        public readonly bool SameFile(Statx other) => Type == other.Type && UserId == other.UserId &&
            _inode == other._inode && _deviceMajor == other._deviceMajor && _deviceMinor == other._deviceMinor;
    }

    internal sealed class SocketLock : SafeHandleMinusOneIsInvalid
    {
        public SocketLock(int fd) : base(ownsHandle: true) => SetHandle(fd);
        protected override bool ReleaseHandle() => CloseFile((int)handle) == 0;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFile(int fd);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Stat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx result);
}
