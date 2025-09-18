using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class FinancialScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Financial";
        public override int Priority => 30;

        public FinancialScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 180f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Financial Statement"] = 45f,
                    ["Audited Financial"] = 50f,
                    ["Budget Report"] = 40f,
                    ["Annual Report"] = 35f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["HasTables"] = 25f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["revenue"] = 2f,
                    ["expense"] = 2f,
                    ["audit"] = 3f,
                    ["budget"] = 2f,
                    ["fiscal"] = 2f
                }
            };
        }
    }
}