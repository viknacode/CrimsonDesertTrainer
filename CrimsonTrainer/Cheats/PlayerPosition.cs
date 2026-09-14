using System.Runtime.InteropServices;
using CrimsonTrainer.Memory;

namespace CrimsonTrainer.Cheats;

public readonly record struct Vector3F(float X, float Y, float Z)
{
    public override string ToString() => $"{X:0.0}, {Y:0.0}, {Z:0.0}";
}

/// <summary>
/// The player's world position (patch 2.01.00). The copy the simulation obeys is the character
/// controller's transform (position at +90/+94/+98), captured by the
/// <see cref="TableScripts.PlayerTransform"/> hook the way mul0's table did it; that is what
/// teleports write. The game mirrors the triple in several other places — three hang off the
/// actor the "Current Player Pointers" hook captures:
/// <code>
///   A  [[[cplayer+68]+48]+18]+44
///   B  [[cplayer+168]+7A0]+3BC
///   C  [[[cplayer+168]+658]+60]+F8
/// </code>
/// and those are written too (plus, when the transform is not captured yet, every other place
/// in the heap holding the same triple, found by scanning and cached).
/// </summary>
internal sealed class PlayerPosition
{
    private static readonly (int Base, int[] Chain, int Final)[] Chains =
    {
        (0x68, new[] { 0x48, 0x18 }, 0x44),
        (0x168, new[] { 0x7A0 }, 0x3BC),
        (0x168, new[] { 0x658, 0x60 }, 0xF8),
    };

    private const long HeapStart = 0x4_0000_0000;

    private const int TransformPosition = 0x90;

    private readonly GameProcess _game;
    private readonly Injection _pointers;
    private readonly Injection _transform;
    private readonly List<nint> _otherCopies = new();

    public PlayerPosition(GameProcess game, Injection playerPointers, Injection playerTransform)
    {
        _game = game;
        _pointers = playerPointers;
        _transform = playerTransform;
    }

    public nint Actor => _pointers.TryReadVar("cplayer", out long actor) ? (nint)actor : 0;

    /// <summary>The controller transform captured by the hook (0 until the game ran the hooked code).</summary>
    public nint Transform => _transform.TryReadVar("pCoords", out long t) ? (nint)t : 0;

    /// <summary>Address of the authoritative X/Y/Z, or 0 while the transform is not captured.</summary>
    public nint TransformPositionAddress => Transform is var t and not 0 ? t + TransformPosition : 0;

    /// <summary>How many copies outside the actor chains were written last time.</summary>
    public int OtherCopies => _otherCopies.Count;

    /// <summary>Addresses of the client-side position copies that currently resolve (0..3 of them).</summary>
    public List<nint> Resolve()
    {
        var found = new List<nint>();
        nint actor = Actor;
        if (actor == 0) return found;
        foreach (var (b, chain, final) in Chains)
        {
            nint p = _game.FollowChain(actor + b, chain, final);
            if (p != 0) found.Add(p);
        }
        return found;
    }

    public Vector3F? Read()
    {
        nint transform = TransformPositionAddress;
        if (transform != 0 && ReadAt(transform) is { } fromTransform) return fromTransform;
        var copies = Resolve();
        if (copies.Count == 0) return null;
        return ReadAt(copies[0]);
    }

    private Vector3F? ReadAt(nint address)
    {
        var b = new byte[12];
        if (!_game.TryRead(address, b, 12)) return null;
        var v = new Vector3F(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8));
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : null;
    }

    /// <summary>
    /// Writes the triple into every copy: the actor chains plus every other place in the heap
    /// holding the current position bit-for-bit (server-side actor). Returns how many were written.
    /// </summary>
    public int Write(Vector3F v)
    {
        nint transform = TransformPositionAddress;
        var chainCopies = Resolve();
        if (transform == 0 && chainCopies.Count == 0) return 0;
        var targets = new List<nint>();
        if (transform != 0) targets.Add(transform);
        targets.AddRange(chainCopies);
        // Without the transform, fall back to every heap copy of the current triple (the old way).
        if (transform == 0 && ReadAt(chainCopies[0]) is { } now) targets.AddRange(FindOtherCopies(now, chainCopies));

        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), v.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), v.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), v.Z);
        foreach (var t in targets.Distinct()) _game.Write(t, bytes);
        return targets.Distinct().Count();
    }

    /// <summary>
    /// Every place in the game heap holding exactly <paramref name="now"/> (other than the
    /// chain copies). Cached: as long as the cached addresses still hold the current value the
    /// scan is skipped; otherwise the heap is rescanned (about a second).
    /// </summary>
    private List<nint> FindOtherCopies(Vector3F now, List<nint> exclude)
    {
        if (_otherCopies.Count > 0 && _otherCopies.All(a => ReadAt(a) == now)) return _otherCopies;
        _otherCopies.Clear();

        int xBits = BitConverter.SingleToInt32Bits(now.X), yBits = BitConverter.SingleToInt32Bits(now.Y), zBits = BitConverter.SingleToInt32Bits(now.Z);
        var buf = new byte[(1 << 24) + 16];
        foreach (var (start, size) in _game.EnumerateReadableRegions(privateOnly: true, 1L << 32))
        {
            if ((long)start < HeapStart) continue;
            for (long off = 0; off < size; off += 1 << 24)
            {
                int len = (int)Math.Min(buf.Length, size - off);
                if (!_game.TryRead(start + (nint)off, buf, len)) break;
                var ints = MemoryMarshal.Cast<byte, int>(buf.AsSpan(0, len & ~3));
                int pos = 0;
                while (true)
                {
                    int i = ints.Slice(pos).IndexOf(xBits);
                    if (i < 0) break;
                    int idx = pos + i; pos = idx + 1;
                    if (idx + 2 >= ints.Length || ints[idx + 1] != yBits || ints[idx + 2] != zBits) continue;
                    nint addr = start + (nint)off + idx * 4;
                    if (!exclude.Contains(addr)) _otherCopies.Add(addr);
                }
            }
        }
        return _otherCopies;
    }
}
