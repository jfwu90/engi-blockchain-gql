﻿using System.ComponentModel.DataAnnotations;

namespace Engi.Substrate.Jobs;

public class FilesRequirement : IScaleSerializable
{
    [Required(AllowEmptyStrings = true)]
    public string IsEditable { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string IsAddable { get; set; } = null!;

    [Required(AllowEmptyStrings = true)]
    public string IsDeletable { get; set; } = null!;

    public void Serialize(ScaleStreamWriter writer)
    {
        writer.Write(IsEditable);
        writer.Write(IsAddable);
        writer.Write(IsDeletable);
    }

    public static FilesRequirement Parse(ScaleStreamReader reader)
    {
        return new()
        {
            IsEditable = reader.ReadString()!,
            IsAddable = reader.ReadString()!,
            IsDeletable = reader.ReadString()!
        };
    }
}