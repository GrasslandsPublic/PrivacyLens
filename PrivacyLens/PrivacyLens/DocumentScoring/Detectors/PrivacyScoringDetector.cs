using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class PrivacyScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Privacy & Terms";
        public override int Priority => 15;

        public PrivacyScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 180f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Privacy Policy"] = 50f,
                    ["Terms of Service"] = 50f,
                    ["Terms of Use"] = 50f,
                    ["GDPR"] = 35f,
                    ["CCPA"] = 35f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Information We Collect"] = 25f,
                    ["How We Use Information"] = 25f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["personal information"] = 3f,
                    ["data collection"] = 3f
                }
            };
        }
    }
}
