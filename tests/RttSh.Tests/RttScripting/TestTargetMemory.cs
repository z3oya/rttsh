using RttSh.Core.Rtt;

namespace Toolbox.Tests.RttScripting;

/// <summary>In-memory ITargetMemory fake: a flat byte array addressed from 0, canned error codes,
/// and halt/resume recording - ScriptRuntime's mem API is tested against this without a DLL.
/// ReadMemory never returns short (the buffer is filled from Data, zero-filled beyond).</summary>
internal sealed class TestTargetMemory : ITargetMemory
{
    /// <summary>Backing store for reads, addressed from 0.</summary>
    public byte[] Data = [];

    /// <summary>When set, ReadMemory/WriteMemory return this code instead of touching the store.</summary>
    public int? ErrorCode;

    /// <summary>The most recent access width seen (1/2/4).</summary>
    public uint LastAccess;

    public readonly List<uint> ReadAddresses = [];
    public readonly List<byte[]> Written = [];

    public bool Halted { get; private set; }
    public int Halts { get; private set; }
    public int Resumes { get; private set; }

    public int ReadMemory(uint address, uint numBytes, byte[] buffer, uint access)
    {
        if (ErrorCode is { } code) return code;
        LastAccess = access;
        ReadAddresses.Add(address);
        for (uint i = 0; i < numBytes; i++)
            buffer[i] = address + i < (uint)Data.Length ? Data[address + i] : (byte)0;
        return (int)numBytes;
    }

    public int WriteMemory(uint address, uint numBytes, byte[] buffer, uint access)
    {
        if (ErrorCode is { } code) return code;
        LastAccess = access;
        var copy = new byte[numBytes];
        Array.Copy(buffer, copy, numBytes);
        Written.Add(copy);
        return (int)numBytes;
    }

    public bool IsHalted() => Halted;

    public void Halt()
    {
        Halted = true;
        Halts++;
    }

    public void Resume()
    {
        Halted = false;
        Resumes++;
    }
}
