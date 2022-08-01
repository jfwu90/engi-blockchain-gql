﻿using Engi.Substrate.Metadata.V14;

namespace Engi.Substrate.Server.Indexing;

public class ExpandedBlock
{
    public string Id { get; init; } = null!;

    public ulong Number { get; set; }

    public DateTime? IndexedOn { get; set; }

    public string Hash { get; set; } = null!;

    public string ParentHash { get; set; } = null!;

    public Extrinsic[] Extrinsics { get; set; } = null!;

    public EventRecord[] Events { get; set; } = null!;

    public DateTime DateTime { get; set; }

    private ExpandedBlock() { }

    public ExpandedBlock(ulong number)
    {
        Id = KeyFrom(number);
        Number = number;
    }

    public void Fill(
        Block block,
        EventRecord[] events,
        RuntimeMetadata meta)
    {
        if (block.Header.Number != Number)
        {
            throw new ArgumentException("Block number doesn't match.", nameof(block));
        }

        var extrinsics = block.Extrinsics
            .Select(extrinsic => Extrinsic.Parse(extrinsic, meta))
            .ToArray();

        Hash = block.Header.Hash.Value;
        ParentHash = block.Header.ParentHash;
        Extrinsics = extrinsics;
        Events = events;
        DateTime = CalculateDateTime(extrinsics);

        IndexedOn = DateTime.UtcNow;
    }

    public static string KeyFrom(ulong number)
    {
        return $"blocks/{number:00000000000000000000}";
    }

    // helpers

    private static DateTime CalculateDateTime(Extrinsic[] extrinsics)
    {
        var setTimeExtrinsic = extrinsics
            .SingleOrDefault(x => x.PalletName == "Timestamp" && x.CallName == "set");

        if (setTimeExtrinsic == null)
        {
            throw new InvalidOperationException("Block does not contain Timestamp.set() extrinsic");
        }

        return (DateTime)setTimeExtrinsic.Fields["now"];
    }
}