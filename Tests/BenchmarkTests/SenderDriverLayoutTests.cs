using System.Reflection.PortableExecutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

public sealed class SenderDriverLayoutTests
{
    [Fact]
    public void PublicAndBetaProfilesUseTheirOwnFunctionLocations()
    {
        var beta = SenderDriverLayout.Resolve(PatchManifest.SupportedCurrentOriginalSha256);
        var current = SenderDriverLayout.Resolve(SenderDriverLayout.PublicOriginal);
        Assert.Equal(0xA48170, beta.CodeRanges[0].Rva);
        Assert.Equal(0xA482E0, current.CodeRanges[0].Rva);
        Assert.Equal(0xA4A730, current.CodeRanges[1].Rva);
        Assert.Equal(current.CodeRanges, SenderDriverLayout.Resolve(SenderDriverLayout.PublicPatched).CodeRanges);
        Assert.Throws<IOException>(() => SenderDriverLayout.Resolve(new string('0', 64)));
        Assert.Throws<IOException>(() => SenderDriverLayout.Resolve(PatchManifest.SupportedStableOriginalSha256));
    }

    [Fact]
    public void LocalPublicFixtureHasDeclaredFunctionBoundariesAndRtti()
    {
        var path = Environment.GetEnvironmentVariable("SLVR_SENDER_PUBLIC_FIXTURE");
        if (string.IsNullOrEmpty(path)) Assert.Skip("Set SLVR_SENDER_PUBLIC_FIXTURE to inspect a local Valve driver; binaries are never committed.");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(SenderDriverLayout.PublicOriginal, hash);
        var layout = SenderDriverLayout.Resolve(hash);
        using var pe = new PEReader(new MemoryStream(bytes));
        var directory = pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var entries = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader(0, directory.Size);
        var functions = new List<(int Begin, int End)>();
        while (entries.RemainingBytes >= 12)
        {
            var begin = entries.ReadInt32();
            var end = entries.ReadInt32();
            entries.ReadInt32(); // Unwind metadata RVA.
            functions.Add((begin, end));
        }
        foreach (var (rva, length) in layout.CodeRanges)
        {
            Assert.Contains((rva, rva + length), functions);
            Assert.Equal(length, pe.GetSectionData(rva).GetContent(0, length).Length);
        }
        // This relocated server routine still references the same singleton global.
        const int serverReference = 0xA40104;
        var instruction = pe.GetSectionData(serverReference).GetContent(0, 7).ToArray();
        Assert.Contains(instruction[0], new byte[] { 0x48, 0x4c });
        Assert.Contains(instruction[1], new byte[] { 0x8b, 0x89 });
        Assert.Equal(5, instruction[2] & 0xc7); // MOV with RIP-relative addressing.
        Assert.Equal(layout.ServerRva, serverReference + 7 + BinaryPrimitives.ReadInt32LittleEndian(instruction.AsSpan(3)));
        var strings = System.Text.Encoding.ASCII.GetString(bytes);
        foreach (var type in new[] { ".?AVSVLServer@@", ".?AVSVLDataLinkUber@@", ".?AVSVLFECRAID6@@" })
            Assert.Contains(type, strings);
    }
}
