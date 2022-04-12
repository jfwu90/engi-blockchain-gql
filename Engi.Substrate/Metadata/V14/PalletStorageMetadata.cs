﻿namespace Engi.Substrate.Metadata.V14;

public class PalletStorageMetadata
{
    public string? Prefix { get; set; }
    public StorageEntryMetadata[]? Items { get; set; }

    public static PalletStorageMetadata Parse(ScaleStream stream)
    {
        return new()
        {
            Prefix = stream.ReadString(),
            Items = stream.ReadList(StorageEntryMetadata.Parse)
        };
    }
}