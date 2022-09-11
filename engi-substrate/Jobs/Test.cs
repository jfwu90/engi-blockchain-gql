﻿using System.ComponentModel.DataAnnotations;
using Engi.Substrate.Metadata.V14;

namespace Engi.Substrate.Jobs;

public class Test : IScaleSerializable
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Id { get; init; } = null!;

    public TestResult AnalysisResult { get; init; }

    public bool Required { get; set; }

    public void Serialize(ScaleStreamWriter writer, RuntimeMetadata _)
    {
        writer.Write(Id);
        writer.Write(AnalysisResult);
        writer.Write(Required);
    }

    public static Test Parse(ScaleStreamReader reader)
    {
        return new()
        {
            Id = reader.ReadString()!,
            AnalysisResult = reader.ReadEnum<TestResult>(),
            Required = reader.ReadBool()
        };
    }
}