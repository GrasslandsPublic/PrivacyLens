using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class FormsScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Forms & Templates";
        public override int Priority => 40;

        public FormsScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 160f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Application Form"] = 45f,
                    ["Registration Form"] = 45f,
                    ["Consent Form"] = 40f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["HasFillableFields"] = 30f,
                    ["HasSignatureBlock"] = 25f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["complete"] = 2f,
                    ["submit"] = 2f
                }
            };
        }
    }
}