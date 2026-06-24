#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ValheimProfiler.Server;

internal enum ServerLogTransportReceiveResult
{
    Complete,
    WaitingForFragments,
    Rejected
}

/// <summary>
/// Bounded transport envelope for Server Log Monitor RPC payloads.
/// The design follows the proven ConditionalConfigSync approach: optional compression,
/// sender-scoped fragmentation, hard payload limits and expiring fragment assemblies.
/// </summary>
internal static class ServerLogTransport
{
    [Flags]
    private enum TransportFlags : byte
    {
        None = 0,
        Compressed = 1,
        Fragmented = 2
    }

    private sealed class FragmentAssembly
    {
        internal long Sender;
        internal int ExpectedFragments;
        internal int ExpectedBytes;
        internal TransportFlags Flags;
        internal long ExpiresUtcTicks;
        internal int ReceivedBytes;
        internal readonly SortedDictionary<int, byte[]> Fragments = new();
    }

    private const int Magic = 0x56504C47; // VPLG
    private const byte EnvelopeVersion = 1;
    private const int CompressionThresholdBytes = 8 * 1024;
    private const int FragmentPayloadBytes = 250000;
    private const int MaxEnvelopeBytes = 300000;
    private const int MaxFragments = 64;
    private const int MaxFragmentAssembliesPerSender = 4;
    private const int MaxFragmentCacheBytesPerSender = 8 * 1024 * 1024;
    private const int MaxFragmentCacheBytesGlobal = 32 * 1024 * 1024;
    private static readonly TimeSpan FragmentLifetime = TimeSpan.FromSeconds(30);

    internal const int MaxRawPayloadBytes = 4 * 1024 * 1024;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, FragmentAssembly> FragmentCache = new();
    private static long _nextPackageId;

    internal static bool Send(long target, string rpcName, ZPackage payload, out string error)
    {
        error = string.Empty;
        try
        {
            ZRoutedRpc routedRpc = ZRoutedRpc.instance;
            if (routedRpc == null)
            {
                error = "ZRoutedRpc is not available.";
                return false;
            }

            if (target == 0L)
            {
                error = "Server Log Monitor transport refuses broadcast target 0.";
                return false;
            }

            if (payload == null)
            {
                error = "Server Log Monitor payload is null.";
                return false;
            }

            byte[] raw = GetPackageBytes(payload);
            if (raw.Length <= 0 || raw.Length > MaxRawPayloadBytes)
            {
                error = $"Server Log Monitor payload size {raw.Length} is outside the allowed range 1..{MaxRawPayloadBytes}.";
                return false;
            }

            TransportFlags flags = TransportFlags.None;
            byte[] encoded = raw;
            if (raw.Length >= CompressionThresholdBytes)
            {
                byte[] compressed = Compress(raw);
                if (compressed.Length + 32 < raw.Length)
                {
                    encoded = compressed;
                    flags |= TransportFlags.Compressed;
                }
            }

            if (encoded.Length > MaxRawPayloadBytes)
            {
                error = $"Encoded Server Log Monitor payload exceeds {MaxRawPayloadBytes} bytes.";
                return false;
            }

            int fragmentCount = (encoded.Length + FragmentPayloadBytes - 1) / FragmentPayloadBytes;
            if (fragmentCount <= 0 || fragmentCount > MaxFragments)
            {
                error = $"Server Log Monitor payload requires invalid fragment count {fragmentCount}.";
                return false;
            }

            if (fragmentCount > 1)
                flags |= TransportFlags.Fragmented;

            long packageId = Interlocked.Increment(ref _nextPackageId);
            for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                int offset = fragmentIndex * FragmentPayloadBytes;
                int count = Math.Min(FragmentPayloadBytes, encoded.Length - offset);
                byte[] fragment = new byte[count];
                Buffer.BlockCopy(encoded, offset, fragment, 0, count);

                var envelope = new ZPackage();
                envelope.Write(Magic);
                envelope.Write(EnvelopeVersion);
                envelope.Write((byte)flags);
                envelope.Write(packageId);
                envelope.Write(fragmentIndex);
                envelope.Write(fragmentCount);
                envelope.Write(encoded.Length);
                envelope.Write(fragment);

                if (envelope.Size() > MaxEnvelopeBytes)
                {
                    error = $"Server Log Monitor fragment {fragmentIndex + 1}/{fragmentCount} exceeded the envelope limit.";
                    return false;
                }

                routedRpc.InvokeRoutedRPC(target, rpcName, envelope);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    internal static ServerLogTransportReceiveResult TryReceive(
        long sender,
        ZPackage envelope,
        out ZPackage payload,
        out string error)
    {
        payload = null;
        error = string.Empty;
        string activeCacheKey = null;

        try
        {
            CleanupExpired();

            if (envelope == null || envelope.Size() <= 0 || envelope.Size() > MaxEnvelopeBytes)
            {
                error = "Invalid or oversized Server Log Monitor transport envelope.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            int magic = envelope.ReadInt();
            byte version = envelope.ReadByte();
            TransportFlags flags = (TransportFlags)envelope.ReadByte();
            long packageId = envelope.ReadLong();
            int fragmentIndex = envelope.ReadInt();
            int fragmentCount = envelope.ReadInt();
            int expectedBytes = envelope.ReadInt();
            byte[] fragmentData = envelope.ReadByteArray();

            if (magic != Magic)
            {
                error = "Unsupported Server Log Monitor transport envelope.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            if (version != EnvelopeVersion)
            {
                error = $"Unsupported Server Log Monitor transport version {version}; expected {EnvelopeVersion}.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            const TransportFlags knownFlags = TransportFlags.Compressed | TransportFlags.Fragmented;
            if ((flags & ~knownFlags) != 0)
            {
                error = $"Unknown Server Log Monitor transport flags {(byte)flags}.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            if (packageId <= 0L || expectedBytes <= 0 || expectedBytes > MaxRawPayloadBytes ||
                fragmentCount <= 0 || fragmentCount > MaxFragments ||
                fragmentIndex < 0 || fragmentIndex >= fragmentCount ||
                fragmentData == null || fragmentData.Length <= 0 || fragmentData.Length > FragmentPayloadBytes ||
                fragmentData.Length > expectedBytes)
            {
                error = "Invalid Server Log Monitor fragment metadata.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            bool fragmented = (flags & TransportFlags.Fragmented) != 0;
            if (!fragmented)
            {
                if (fragmentCount != 1 || fragmentIndex != 0 || fragmentData.Length != expectedBytes)
                {
                    error = "Invalid unfragmented Server Log Monitor envelope.";
                    return ServerLogTransportReceiveResult.Rejected;
                }

                return DecodePayload(flags, fragmentData, out payload, out error);
            }

            int expectedFragmentCount = (expectedBytes + FragmentPayloadBytes - 1) / FragmentPayloadBytes;
            int expectedFragmentLength = fragmentIndex == fragmentCount - 1
                ? expectedBytes - FragmentPayloadBytes * (fragmentCount - 1)
                : FragmentPayloadBytes;
            if (fragmentCount <= 1 ||
                fragmentCount != expectedFragmentCount ||
                expectedFragmentLength <= 0 ||
                fragmentData.Length != expectedFragmentLength)
            {
                error = "Invalid fragmented Server Log Monitor envelope shape.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            activeCacheKey = sender + ":" + packageId;
            byte[] completed = null;

            lock (CacheLock)
            {
                if (!FragmentCache.TryGetValue(activeCacheKey, out FragmentAssembly assembly))
                {
                    if (GetAssemblyCountForSender(sender) >= MaxFragmentAssembliesPerSender)
                    {
                        error = "Too many pending fragmented Server Log Monitor payloads from this sender.";
                        return ServerLogTransportReceiveResult.Rejected;
                    }

                    if (GetCacheBytesForSender(sender) + fragmentData.Length > MaxFragmentCacheBytesPerSender ||
                        GetCacheBytesGlobal() + fragmentData.Length > MaxFragmentCacheBytesGlobal)
                    {
                        error = "Server Log Monitor fragment cache limit exceeded.";
                        return ServerLogTransportReceiveResult.Rejected;
                    }

                    assembly = new FragmentAssembly
                    {
                        Sender = sender,
                        ExpectedFragments = fragmentCount,
                        ExpectedBytes = expectedBytes,
                        Flags = flags,
                        ExpiresUtcTicks = DateTime.UtcNow.Add(FragmentLifetime).Ticks
                    };
                    FragmentCache.Add(activeCacheKey, assembly);
                }
                else if (assembly.ExpectedFragments != fragmentCount ||
                         assembly.ExpectedBytes != expectedBytes ||
                         assembly.Flags != flags)
                {
                    RemoveAssembly(activeCacheKey);
                    error = "Inconsistent Server Log Monitor fragment metadata.";
                    return ServerLogTransportReceiveResult.Rejected;
                }

                if (assembly.Fragments.ContainsKey(fragmentIndex))
                {
                    RemoveAssembly(activeCacheKey);
                    error = "Duplicate Server Log Monitor fragment received.";
                    return ServerLogTransportReceiveResult.Rejected;
                }

                if (GetCacheBytesForSender(sender) + fragmentData.Length > MaxFragmentCacheBytesPerSender ||
                    GetCacheBytesGlobal() + fragmentData.Length > MaxFragmentCacheBytesGlobal ||
                    assembly.ReceivedBytes + fragmentData.Length > assembly.ExpectedBytes)
                {
                    RemoveAssembly(activeCacheKey);
                    error = "Server Log Monitor fragment cache size limit exceeded.";
                    return ServerLogTransportReceiveResult.Rejected;
                }

                assembly.Fragments.Add(fragmentIndex, fragmentData);
                assembly.ReceivedBytes += fragmentData.Length;
                assembly.ExpiresUtcTicks = DateTime.UtcNow.Add(FragmentLifetime).Ticks;

                if (assembly.Fragments.Count < assembly.ExpectedFragments)
                    return ServerLogTransportReceiveResult.WaitingForFragments;

                if (assembly.ReceivedBytes != assembly.ExpectedBytes)
                {
                    RemoveAssembly(activeCacheKey);
                    error = "Completed Server Log Monitor fragment assembly has an invalid size.";
                    return ServerLogTransportReceiveResult.Rejected;
                }

                completed = new byte[assembly.ExpectedBytes];
                int destinationOffset = 0;
                for (int i = 0; i < assembly.ExpectedFragments; i++)
                {
                    if (!assembly.Fragments.TryGetValue(i, out byte[] part))
                    {
                        RemoveAssembly(activeCacheKey);
                        error = "Completed Server Log Monitor fragment assembly is missing a fragment.";
                        return ServerLogTransportReceiveResult.Rejected;
                    }

                    Buffer.BlockCopy(part, 0, completed, destinationOffset, part.Length);
                    destinationOffset += part.Length;
                }

                RemoveAssembly(activeCacheKey);
                activeCacheKey = null;
            }

            return DecodePayload(flags, completed, out payload, out error);
        }
        catch (Exception ex)
        {
            if (activeCacheKey != null)
            {
                lock (CacheLock)
                    RemoveAssembly(activeCacheKey);
            }

            error = ex.GetType().Name + ": " + ex.Message;
            return ServerLogTransportReceiveResult.Rejected;
        }
    }

    internal static void Clear()
    {
        lock (CacheLock)
            FragmentCache.Clear();
    }

    private static ServerLogTransportReceiveResult DecodePayload(
        TransportFlags flags,
        byte[] encoded,
        out ZPackage payload,
        out string error)
    {
        payload = null;
        error = string.Empty;
        try
        {
            byte[] raw = (flags & TransportFlags.Compressed) != 0
                ? DecompressLimited(encoded, MaxRawPayloadBytes)
                : encoded;

            if (raw.Length <= 0 || raw.Length > MaxRawPayloadBytes)
            {
                error = $"Decoded Server Log Monitor payload size {raw.Length} is invalid.";
                return ServerLogTransportReceiveResult.Rejected;
            }

            payload = new ZPackage(raw);
            return ServerLogTransportReceiveResult.Complete;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return ServerLogTransportReceiveResult.Rejected;
        }
    }


    private static byte[] GetPackageBytes(ZPackage package)
    {
        int size = package.Size();
        byte[] data = package.GetArray();
        if (data.Length == size)
            return data;
        if (data.Length < size)
            throw new InvalidDataException($"ZPackage returned {data.Length} bytes for declared size {size}.");

        byte[] exact = new byte[size];
        Buffer.BlockCopy(data, 0, exact, 0, size);
        return exact;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] DecompressLimited(byte[] data, int maximumBytes)
    {
        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];

        for (;;)
        {
            int read = deflate.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"Decompressed Server Log Monitor payload exceeds {maximumBytes} bytes.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static void CleanupExpired()
    {
        long now = DateTime.UtcNow.Ticks;
        lock (CacheLock)
        {
            foreach (string key in FragmentCache
                         .Where(pair => pair.Value.ExpiresUtcTicks <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                FragmentCache.Remove(key);
            }
        }
    }

    private static int GetAssemblyCountForSender(long sender) =>
        FragmentCache.Values.Count(assembly => assembly.Sender == sender);

    private static int GetCacheBytesForSender(long sender) =>
        FragmentCache.Values.Where(assembly => assembly.Sender == sender).Sum(assembly => assembly.ReceivedBytes);

    private static int GetCacheBytesGlobal() =>
        FragmentCache.Values.Sum(assembly => assembly.ReceivedBytes);

    private static void RemoveAssembly(string cacheKey)
    {
        if (!string.IsNullOrEmpty(cacheKey))
            FragmentCache.Remove(cacheKey);
    }
}
