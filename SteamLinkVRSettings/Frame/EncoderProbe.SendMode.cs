using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace SteamLinkVRSettings.Frame;

public sealed partial class EncoderProbe
{
    // asyncSend is consumed by the FEC constructor, not by bitrate refresh.
    // Observe the existing object; never write private fields or create a worker.
    internal async Task<bool> ReadActiveSendModeAsync()
    {
        await _gate.WaitAsync();
        try { return await Task.Run(ReadActiveSendMode); }
        finally { _gate.Release(); }
    }

    private bool ReadActiveSendMode()
    {
        using var process = DiscoverProcess() ?? throw new IOException("SteamVR is not running.");
        var session = _session ?? throw new IOException("Start encoder monitoring first.");
        if (process.Id != session.ProcessId) throw new IOException("SteamVR changed while checking the sender.");
        var driver = ReadModules(process).Single(m => m.Name.Equals("driver_vrlink.dll", StringComparison.OrdinalIgnoreCase));
        var hash = FileHash(driver.Path);
        var layout = SenderDriverLayout.Resolve(hash);
        using var handle = OpenProcess(0x410, false, process.Id);
        if (handle.IsInvalid) ThrowWin32();
        ValidateIdentity(handle, session.CreationTime);
        byte[] Read(nint address, int size)
        {
            if (address < 0x10000 || (long)address > 0x7fffffffffff - size || size is < 1 or > 256)
                throw new IOException("Invalid sender metadata address.");
            var value = new byte[size];
            if (!ReadProcessMemory(handle, address, value, (nuint)size, out var count) || count != (nuint)size) ThrowWin32();
            return value;
        }
        nint Pointer(nint address) => checked((nint)BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8)));
        bool InImage(nint address, int size) => address >= driver.Base && (long)address - (long)driver.Base <= driver.Size - size;
        using var file = File.OpenRead(driver.Path);
        using var pe = new PEReader(file);
        foreach (var (rva, length) in layout.CodeRanges)
            for (int at = 0; at < length; at += 32)
            {
                var count = Math.Min(32, length - at);
                if (!InImage(driver.Base + rva + at, count) ||
                    !Read(driver.Base + rva + at, count).SequenceEqual(pe.GetSectionData(rva + at).GetContent(0, count)))
                    throw new IOException("The live sender implementation does not match the verified driver.");
            }
        void Type(nint instance, string expected)
        {
            var table = Pointer(instance);
            if (!InImage(table - 8, 16)) throw new IOException("Unsupported sender object.");
            var locator = Pointer(table - 8);
            if (!InImage(locator, 24)) throw new IOException("Unsupported sender type metadata.");
            var raw = Read(locator, 24);
            uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
            var descriptor = driver.Base + checked((int)U32(12));
            if (U32(0) != 1 || U32(4) != 0 || U32(8) != 0 || locator - checked((int)U32(20)) != driver.Base || !InImage(descriptor, 112))
                throw new IOException("Unsupported sender type layout.");
            if (Encoding.ASCII.GetString(Read(descriptor + 16, 96)).Split('\0')[0] != expected)
                throw new IOException("Unexpected active sender type.");
        }
        var server = Pointer(driver.Base + layout.ServerRva); Type(server, ".?AVSVLServer@@");
        var link = Pointer(server + 392); Type(link, ".?AVSVLDataLinkUber@@");
        var fec = Pointer(link + 16); Type(fec, ".?AVSVLFECRAID6@@");
        var mode = Read(fec + 16, 1)[0];
        if (mode > 1 || Pointer(fec + 3568) != server || Pointer(driver.Base + layout.ServerRva) != server ||
            Pointer(server + 392) != link || Pointer(link + 16) != fec || Read(fec + 16, 1)[0] != mode)
            throw new IOException("The sender changed while being measured.");
        ValidateIdentity(handle, session.CreationTime);
        return mode != 0;
    }
}
