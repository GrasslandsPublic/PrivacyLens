using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class SecurityScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Information Security";
        public override int Priority => 25;

        public SecurityScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 200f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Information Security Policy"] = 45f,
                    ["System Security Plan"] = 45f,
                    ["ISMS"] = 40f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Access Control"] = 20f,
                    ["Risk Assessment"] = 20f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["vulnerability"] = 2f,
                    ["threat"] = 2f
                }
            };
        }
    }
}
