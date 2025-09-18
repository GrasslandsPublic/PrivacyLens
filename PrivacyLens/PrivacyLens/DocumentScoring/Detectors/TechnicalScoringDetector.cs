using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class TechnicalScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Technical";
        public override int Priority => 20;

        public TechnicalScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 200f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["API Documentation"] = 45f,
                    ["SDK"] = 40f,
                    ["Technical Reference"] = 40f,
                    ["User Guide"] = 35f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["HasCodeBlocks"] = 25f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["endpoint"] = 3f,
                    ["api"] = 3f,
                    ["database"] = 2f
                }
            };
        }
    }
}
