using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using PrivacyLens.DocumentScoring.Core;
using PrivacyLens.DocumentScoring.Models;

namespace PrivacyLens.DocumentScoring.Detectors
{
    public class BoardScoringDetector : BaseScoringDetector
    {
        public override string DocumentType => "Board Documents";
        public override int Priority => 35;

        public BoardScoringDetector(ILogger logger = null) : base(logger) { }

        protected override ScoringProfile GetScoringProfile()
        {
            return new ScoringProfile
            {
                DocumentType = DocumentType,
                MaxPossibleScore = 150f,

                DefinitiveFeatures = new Dictionary<string, float>
                {
                    ["Board Minutes"] = 45f,
                    ["Meeting Minutes"] = 40f,
                    ["Board Agenda"] = 40f
                },

                StructuralFeatures = new Dictionary<string, float>
                {
                    ["Call to Order"] = 20f,
                    ["Adjournment"] = 15f
                },

                LexicalFeatures = new Dictionary<string, float>
                {
                    ["motion"] = 3f,
                    ["seconded"] = 3f
                }
            };
        }
    }
}
