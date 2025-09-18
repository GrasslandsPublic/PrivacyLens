using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class WebScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Web Content";
        public override int Priority => 50;

        public WebScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 100f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["<html"] = 50f,
                    ["<!DOCTYPE"] = 50f,
                    ["<body"] = 30f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["HasHTMLTags"] = 30f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["href"] = 2f,
                    ["http"] = 2f
                }
            };
        }

        public override bool CanHandleDocument(DocumentMetadata metadata)
        {
            if (metadata?.Source == "Web") return true;
            if (metadata?.FileType == ".html" || metadata?.FileType == ".htm") return true;

            return base.CanHandleDocument(metadata);
        }
    }
}