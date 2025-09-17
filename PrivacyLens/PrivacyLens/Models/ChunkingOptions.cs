// File: Models/ChunkingOptions.cs
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace PrivacyLens.DocumentProcessing.Models
{
    public class ChunkingOptions
    {
        public const string SectionName = "ChunkingOptions";

        public int TargetChunkSize { get; set; } = 500;
        public int MinChunkSize { get; set; } = 200;
        public int MaxChunkSize { get; set; } = 800;
        public int OverlapTokens { get; set; } = 50;
        public DocumentTypeProfiles Profiles { get; set; } = new();
        public Dictionary<string, object> StrategySpecificOptions { get; set; } = new();
    }

    public class DocumentTypeProfiles
    {
        public ChunkingProfile Legal { get; set; } = new();
        public ChunkingProfile Technical { get; set; } = new();
        public ChunkingProfile Policy { get; set; } = new();
        public ChunkingProfile Html { get; set; } = new();
        public ChunkingProfile Markdown { get; set; } = new();
        public ChunkingProfile Default { get; set; } = new();
    }

    public class ChunkingProfile
    {
        public int TargetChunkSize { get; set; }
        public int MinChunkSize { get; set; }
        public int MaxChunkSize { get; set; }
        public int OverlapTokens { get; set; }
        public Dictionary<string, object> AdditionalSettings { get; set; } = new();
    }

    // Factory service for getting appropriate options
    public interface IChunkingOptionsFactory
    {
        ChunkingOptions GetOptions(string documentType = null);
        ChunkingProfile GetProfile(string documentType);
    }

    public class ChunkingOptionsFactory : IChunkingOptionsFactory
    {
        private readonly ChunkingOptions _options;

        public ChunkingOptionsFactory(IOptions<ChunkingOptions> options)
        {
            _options = options.Value;
        }

        public ChunkingOptions GetOptions(string documentType = null)
        {
            if (string.IsNullOrEmpty(documentType))
                return _options;

            var profile = GetProfile(documentType);

            // Create a copy with profile-specific overrides
            return new ChunkingOptions
            {
                TargetChunkSize = profile.TargetChunkSize > 0 ? profile.TargetChunkSize : _options.TargetChunkSize,
                MinChunkSize = profile.MinChunkSize > 0 ? profile.MinChunkSize : _options.MinChunkSize,
                MaxChunkSize = profile.MaxChunkSize > 0 ? profile.MaxChunkSize : _options.MaxChunkSize,
                OverlapTokens = profile.OverlapTokens > 0 ? profile.OverlapTokens : _options.OverlapTokens,
                StrategySpecificOptions = profile.AdditionalSettings ?? _options.StrategySpecificOptions,
                Profiles = _options.Profiles
            };
        }

        public ChunkingProfile GetProfile(string documentType)
        {
            return documentType?.ToLowerInvariant() switch
            {
                "legal" => _options.Profiles.Legal,
                "technical" => _options.Profiles.Technical,
                "policy" => _options.Profiles.Policy,
                "html" => _options.Profiles.Html,
                "markdown" => _options.Profiles.Markdown,
                _ => _options.Profiles.Default
            };
        }
    }
}
