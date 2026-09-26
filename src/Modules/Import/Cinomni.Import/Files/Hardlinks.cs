using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cinomni.Import.Files;

/// <summary>Whether a hardlink is possible between two storage roots, and the reason when it is not.</summary>
/// <param name="CanHardlink">True when a zero-byte probe file was linked from one root into the other.</param>
/// <param name="Explanation">
/// A short, path-free sentence naming the platform error class. It is written to be logged as is, so it
/// carries no path and no credential — only the outcome and its cause.
/// </param>
public sealed record HardlinkProbeResult(bool CanHardlink, string Explanation);

/// <summary>
/// The one filesystem question the storage contract turns on: can a file in the download staging root be
/// <em>hardlinked</em> into the library root? A hardlink means the imported file shares its bytes with the
/// still-seeding download; a copy means the installation quietly holds two full copies of everything.
/// <para>
/// The only honest answer is to attempt <c>link()</c> and read the error. Device numbers must not be
/// compared: separate Docker volumes on one underlying filesystem report the same device number and still
/// refuse a link across the mount points, so a <c>st_dev</c> comparison would report success for exactly
/// the deployment this exists to catch.
/// </para>
/// <para>
/// <see cref="AreSameFile"/> answers a different question about two files that already exist, and is the
/// one the recovery path needs. "Are these two paths one file?" is settled by the identity the filesystem
/// itself keeps — volume plus inode — which is exact, is about those two files rather than about what
/// their directories are capable of, and costs no write.
/// </para>
/// <para>
/// This lives beside <see cref="LocalImportFileSystem"/> because Import is the module that hardlinks, and
/// the Host's startup contract asks the same question about the same two configured roots. It is the one
/// interop point either of them needs.
/// </para>
/// </summary>
public static class Hardlinks
{
    /// <summary>The current directory, for a path-based <c>statx</c> that resolves relative paths itself.</summary>
    private const int AtFdCwd = -100;

    /// <summary><c>STATX_INO</c>: the only field asked for, so the kernel does the least work it can.</summary>
    private const uint StatxIno = 0x0000_0100;

    /// <summary>
    /// Offsets into <c>struct statx</c>, whose layout the kernel fixes identically on every architecture —
    /// which is exactly why it is used here instead of <c>stat</c>, whose layout is per-architecture.
    /// </summary>
    private const int StatxMaskOffset = 0;
    private const int StatxInoOffset = 32;
    private const int StatxDeviceMajorOffset = 136;
    private const int StatxDeviceMinorOffset = 140;

    /// <summary>Room for every field of <c>struct statx</c> plus the spares it reserves for growth.</summary>
    private const int StatxBufferBytes = 256;

    /// <summary>
    /// Offsets into Windows' <c>BY_HANDLE_FILE_INFORMATION</c>: the attribute word, three file times, the
    /// volume serial, the two halves of the size, the link count, and the two halves of the file index.
    /// </summary>
    private const int VolumeSerialNumberOffset = 28;
    private const int FileIndexHighOffset = 44;
    private const int FileIndexLowOffset = 48;

    /// <summary>The whole of <c>BY_HANDLE_FILE_INFORMATION</c>: thirteen 32-bit fields.</summary>
    private const int ByHandleFileInformationBytes = 52;

    /// <summary>
    /// Attempts a hardlink and reports the platform error code when it fails. Never throws for an
    /// ordinary filesystem refusal: the outcome is the return value, and <paramref name="errorCode"/>
    /// carries whatever the platform said about it (0 when the platform reported nothing).
    /// </summary>
    public static bool TryLink(string fromPath, string toPath, out int errorCode)
    {
        errorCode = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(toPath, fromPath, IntPtr.Zero))
                {
                    return true;
                }

                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            if (Link(fromPath, toPath) == 0)
            {
                return true;
            }

            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No link() at all on this platform, which is the same answer as a refusal: copy instead.
            return false;
        }
    }

    /// <summary>
    /// Asks whether a hardlink can be made from <paramref name="sourceRoot"/> into
    /// <paramref name="targetRoot"/> by linking a zero-byte probe file between them and removing both
    /// again. It costs no bytes and leaves nothing behind, so it is cheap enough to run once at startup —
    /// which is the only thing that runs it. Deciding what an interrupted attempt already left on disk is
    /// <see cref="AreSameFile"/>'s job: a write into a directory a torrent is seeding from has no place on
    /// a recovery path, and capability is the wrong question about a file that already exists.
    /// <para>
    /// Never throws. A source root that cannot even hold the probe file is reported as "cannot
    /// hardlink", because that is the outcome an import would get.
    /// </para>
    /// </summary>
    public static HardlinkProbeResult Probe(string sourceRoot, string targetRoot)
    {
        var name = $".cinomni-link-probe-{Guid.NewGuid():N}";
        var source = Path.Combine(sourceRoot, name);
        var target = Path.Combine(targetRoot, name);

        try
        {
            File.WriteAllBytes(source, []);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return new HardlinkProbeResult(
                false,
                $"a zero-byte probe file could not be created in the source root ({exception.GetType().Name})");
        }

        try
        {
            return TryLink(source, target, out var errorCode)
                ? new HardlinkProbeResult(true, "a hardlink between the two roots succeeded")
                : new HardlinkProbeResult(false, $"link() failed with {Describe(errorCode)}");
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    /// <summary>
    /// Whether two existing paths are one and the same file — which is precisely what "was this
    /// hardlinked or copied?" means once both files are on disk. Nothing is written, opened for writing
    /// or created: the source is a file a torrent may still be seeding from, and a question about it must
    /// not be an operation on it.
    /// <para>
    /// Returns <see langword="null"/>, and never a guess, when the platform cannot supply the identity —
    /// a filesystem that numbers no files, a kernel without <c>statx</c>, an operating system this
    /// project does not target, or a file that went away between the two reads. The caller has to say
    /// "unknown" rather than record an operation that may contradict what happened to the file.
    /// </para>
    /// <para>
    /// Comparing identity is sound where comparing capability is not: two paths with the same volume and
    /// the same inode are the same file however many mount points reach it, whereas two distinct volumes
    /// that report one device number say nothing about whether a link between them would be allowed.
    /// </para>
    /// </summary>
    public static bool? AreSameFile(string firstPath, string secondPath)
    {
        var first = TryIdentify(firstPath);
        var second = TryIdentify(secondPath);
        return first is null || second is null ? null : first == second;
    }

    /// <summary>
    /// Names the error class an operator has to act on, for the platform this process is running on.
    /// The two that matter in a container are a second filesystem and an account that may not link, and
    /// they need opposite fixes — one is the mount layout, the other is the uid the two containers run as.
    /// </summary>
    public static string Describe(int errorCode) =>
        OperatingSystem.IsWindows() ? DescribeWindowsError(errorCode) : DescribeUnixError(errorCode);

    /// <summary>
    /// The Unix half of <see cref="Describe"/>, reachable from any platform so the deployment target's
    /// error classes can be exercised on a developer's machine that cannot produce them.
    /// </summary>
    public static string DescribeUnixError(int errorNumber) => errorNumber switch
    {
        0 => "no error code, so the platform reported nothing usable",
        1 => "EPERM (1): the account this process runs as may not link the file, which is what a uid "
             + "mismatch looks like under fs.protected_hardlinks",
        13 => "EACCES (13): the account this process runs as may not write into the target root",
        18 => "EXDEV (18): the two roots are on different filesystems",
        31 => "EMLINK (31): the source file already holds as many links as this filesystem allows",
        _ => $"errno {errorNumber}",
    };

    /// <summary>The Windows half of <see cref="Describe"/>, for a development installation.</summary>
    public static string DescribeWindowsError(int errorCode) => errorCode switch
    {
        1 => "ERROR_INVALID_FUNCTION (1): this filesystem does not support hardlinks",
        5 => "ERROR_ACCESS_DENIED (5): this account may not link the file",
        17 => "ERROR_NOT_SAME_DEVICE (17): the two roots are on different volumes",
        _ => $"Windows error {errorCode}",
    };

    /// <summary>
    /// The filesystem's own identity for a file: the volume it lives on and its number within that
    /// volume. Two paths that share both are one file; anything else is two files.
    /// </summary>
    private readonly record struct FileIdentity(ulong Volume, ulong Number);

    private static FileIdentity? TryIdentify(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIdentity(path);
        }

        return OperatingSystem.IsLinux() ? LinuxIdentity(path) : null;
    }

    /// <summary>
    /// Reads the volume serial and file index the volume keeps for an open handle. The handle is opened
    /// for reading and shared for writing and deletion, so it never blocks the download that is still
    /// writing to the file.
    /// </summary>
    private static FileIdentity? WindowsIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var information = new byte[ByHandleFileInformationBytes];
            if (!GetFileInformationByHandle(handle, information))
            {
                return null;
            }

            var number = ((ulong)BitConverter.ToUInt32(information, FileIndexHighOffset) << 32)
                | BitConverter.ToUInt32(information, FileIndexLowOffset);

            // A filesystem that numbers no files (the FAT family, which has no hardlinks either) reports
            // zero for every file, and zero would make every file look like every other one.
            return number == 0
                ? null
                : new FileIdentity(BitConverter.ToUInt32(information, VolumeSerialNumberOffset), number);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the device and inode through <c>statx</c>, which reports them in a layout the kernel keeps
    /// identical on every architecture. The file is not opened at all.
    /// </summary>
    private static FileIdentity? LinuxIdentity(string path)
    {
        try
        {
            var buffer = new byte[StatxBufferBytes];
            if (Statx(AtFdCwd, path, 0, StatxIno, buffer) != 0)
            {
                return null;
            }

            // The kernel answers with the fields it actually filled in, and is entitled to fill in fewer.
            if ((BitConverter.ToUInt32(buffer, StatxMaskOffset) & StatxIno) == 0)
            {
                return null;
            }

            var device = ((ulong)BitConverter.ToUInt32(buffer, StatxDeviceMajorOffset) << 32)
                | BitConverter.ToUInt32(buffer, StatxDeviceMinorOffset);
            return new FileIdentity(device, BitConverter.ToUInt64(buffer, StatxInoOffset));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A C library without statx cannot be asked; "unknown" is the honest answer.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            // A probe file that cannot be removed must not fail the check it was created for. The
            // writability check that runs beside this one is what reports an unusable root.
        }
    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, byte[] lpFileInformation);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string oldpath, string newpath);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, string pathname, int flags, uint mask, byte[] statxbuf);
}
